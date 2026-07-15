using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Domain;
using Payments.Infrastructure;

namespace Payments.Api.Demo;

public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    /// <summary>Off unless something asks for it — the compose demo profile does; tests never do.</summary>
    public bool Seed { get; set; }
}

/// <summary>
/// Seeds a spread of payments so the dashboard has something to show on first run.
///
/// This exists because create is an API-only operation: the dashboard lists, captures and
/// refunds, but has no "new payment" form. Without seed data a reviewer's first view is an
/// empty table with no way to fill it, which is a poor demo of a working system.
///
/// It is deliberately not test fixture data and not a migration: it is gated behind
/// <c>Demo:Seed</c>, it no-ops if any payment already exists (so restarting the stack doesn't
/// pile up duplicates), and it goes through the real aggregate — every seeded payment has a
/// genuine audit trail rather than rows conjured straight into the table.
/// </summary>
public sealed class DemoDataSeeder(
    IServiceScopeFactory scopes,
    IOptions<DemoOptions> options,
    TimeProvider clock,
    ILogger<DemoDataSeeder> logger) : IHostedService
{
    private const string MerchantId = "acme";
    private const string Actor = "merchant:acme";

    public async Task StartAsync(CancellationToken ct)
    {
        if (!options.Value.Seed)
            return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var payments = scope.ServiceProvider.GetRequiredService<IPaymentRepository>();
        var settlement = scope.ServiceProvider.GetRequiredService<ISettlementQueue>();

        if (await db.Payments.AnyAsync(ct))
        {
            logger.LogInformation("Demo seed skipped: payments already exist.");
            return;
        }

        var now = clock.GetUtcNow();
        var seeded = 0;

        foreach (var spec in Specs())
        {
            // Backdated so the list has a realistic spread of "created" times rather than
            // twelve payments sharing one timestamp.
            var createdAt = now.AddMinutes(-spec.MinutesAgo);
            var correlationId = Guid.NewGuid().ToString("N");

            var payment = Payment.Create(
                MerchantId, spec.AmountMinor, "USD", spec.Last4, spec.Brand, spec.Description,
                Actor, correlationId, createdAt);

            if (spec.Outcome is Outcome.Declined)
            {
                payment.Fail(Actor, correlationId, "card declined", createdAt.AddSeconds(1));
            }
            else
            {
                payment.Authorize(Actor, correlationId, createdAt.AddSeconds(1));

                if (spec.Outcome is Outcome.Captured or Outcome.Refunded)
                {
                    var captureCorrelation = Guid.NewGuid().ToString("N");
                    payment.Capture(Actor, captureCorrelation, createdAt.AddSeconds(30));

                    if (spec.Outcome is Outcome.Refunded)
                    {
                        payment.Refund(Actor, Guid.NewGuid().ToString("N"), "customer request",
                            createdAt.AddMinutes(2));
                    }
                    else
                    {
                        // Left for the worker to settle, exactly as a real capture would be —
                        // so the queue has work the moment the stack comes up.
                        settlement.Enqueue(payment.Id, captureCorrelation, createdAt.AddSeconds(30));
                    }
                }
            }

            payments.Add(payment);
            await payments.SaveAsync(payment, ct);
            seeded++;
        }

        logger.LogInformation("Demo seed inserted {Count} payments.", seeded);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private enum Outcome { Authorized, Captured, Refunded, Declined }

    private sealed record Spec(int MinutesAgo, long AmountMinor, string Description, Outcome Outcome,
        string Last4 = "4242", string Brand = "visa");

    /// <summary>
    /// A deliberate mix. Several are left <c>Authorized</c> so a reviewer has something to press
    /// Capture on and can watch the timeline grow to Settled by itself; the Captured ones give
    /// the worker a queue to drain on startup.
    /// </summary>
    private static IEnumerable<Spec> Specs() =>
    [
        new(2, 4200, "Order #1042 — headphones", Outcome.Authorized),
        new(9, 15990, "Order #1041 — annual plan", Outcome.Authorized),
        new(17, 899, "Order #1040 — coffee beans", Outcome.Authorized),
        new(26, 7350, "Order #1039 — desk lamp", Outcome.Captured),
        new(38, 24500, "Order #1038 — monitor stand", Outcome.Captured, "4444", "mastercard"),
        new(55, 1299, "Order #1037 — cable", Outcome.Captured),
        new(72, 5600, "Order #1036 — keyboard", Outcome.Refunded),
        new(94, 33000, "Order #1035 — chair", Outcome.Refunded, "0005", "amex"),
        new(120, 2450, "Order #1034 — mouse mat", Outcome.Declined, "0002"),
        new(151, 18750, "Order #1033 — dock", Outcome.Declined, "4444", "mastercard"),
        new(188, 9900, "Order #1032 — webcam", Outcome.Captured),
        new(240, 640, "Order #1031 — stickers", Outcome.Authorized),
    ];
}
