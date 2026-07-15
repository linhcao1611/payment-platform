using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Domain;
using Payments.Infrastructure;
using Payments.Infrastructure.Entities;

namespace Payments.Api.Demo;

public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    /// <summary>Off unless something asks for it — the compose demo profile does; tests never do.</summary>
    public bool Seed { get; set; }

    /// <summary>Days of history to fabricate, ending now.</summary>
    public int Days { get; set; } = 7;

    /// <summary>Payments on a typical weekday. Weekends get roughly 55% of this.</summary>
    public int PaymentsPerDay { get; set; } = 70;
}

/// <summary>
/// Seeds a week of plausible payment history so the dashboard, its filters and its paging have
/// something real to work on.
///
/// This exists because create is an API-only operation: the dashboard lists, captures and
/// refunds, but has no "new payment" form. Without seed data a reviewer's first view is an
/// empty table with no way to fill it.
///
/// Three things it is careful about:
///
/// <list type="bullet">
/// <item><b>History is settled in the past, not by the worker.</b> A week of captures all
/// enqueued as live jobs would flood the worker on startup and stamp every settlement with
/// today's timestamp. Historical payments walk their lifecycle directly with historical times;
/// only the last few minutes' worth get a real job for the worker to find.</item>
/// <item><b>It never touches the metrics.</b> The counters are incremented in the controller,
/// so fabricated history stays out of Prometheus. Seeded rows are demo data; invented
/// throughput graphs would be a lie.</item>
/// <item><b>It goes through the real aggregate</b> — every payment has a genuine audit trail
/// with correlation ids, not rows conjured straight into the table.</item>
/// </list>
///
/// Gated behind <c>Demo:Seed</c> and skipped entirely if any payment already exists, so
/// restarting the stack doesn't pile up duplicates.
/// </summary>
public sealed class DemoDataSeeder(
    IServiceScopeFactory scopes,
    IOptions<DemoOptions> options,
    TimeProvider clock,
    ILogger<DemoDataSeeder> logger) : IHostedService
{
    private const string MerchantId = "acme";
    private const string Actor = "merchant:acme";

    /// <summary>Fixed so every reviewer sees the same week. A demo that reshuffles is harder to talk about.</summary>
    private const int RandomSeed = 20260715;

    /// <summary>Anything older than this has finished whatever it was going to do.</summary>
    private static readonly TimeSpan SettlesWithin = TimeSpan.FromMinutes(90);

    public async Task StartAsync(CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Seed)
            return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        if (await db.Payments.AnyAsync(ct))
        {
            logger.LogInformation("Demo seed skipped: payments already exist.");
            return;
        }

        var random = new Random(RandomSeed);
        var now = clock.GetUtcNow();
        var counts = new Dictionary<PaymentStatus, int>();
        var jobs = 0;

        foreach (var createdAt in Timeline(settings, now, random))
        {
            var (payment, job) = BuildPayment(createdAt, now, random);

            // Added straight to the context rather than one SaveAsync per payment: a week is
            // several hundred payments and as many round trips, which would visibly stall
            // startup. The rule that a payment is never saved without its transitions still
            // holds — they go into the same SaveChanges below.
            db.Payments.Add(payment);
            db.PaymentTransitions.AddRange(payment.PendingTransitions);
            payment.ClearPendingTransitions();

            if (job is not null)
            {
                db.SettlementJobs.Add(job);
                jobs++;
            }

            counts[payment.Status] = counts.GetValueOrDefault(payment.Status) + 1;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Demo seed inserted {Count} payments across {Days} days ({Breakdown}) and {Jobs} settlement jobs.",
            counts.Values.Sum(),
            settings.Days,
            string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}")),
            jobs);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Creation times across the window, shaped like a real merchant's traffic: busy in the
    /// working day, quiet overnight, lighter at weekends. Flat random timestamps would make the
    /// list and any time filter look obviously synthetic.
    /// </summary>
    private static IEnumerable<DateTimeOffset> Timeline(DemoOptions settings, DateTimeOffset now, Random random)
    {
        var times = new List<DateTimeOffset>();

        for (var dayOffset = settings.Days - 1; dayOffset >= 0; dayOffset--)
        {
            var day = now.AddDays(-dayOffset);
            var isWeekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            // ±20% day-to-day wobble, so no two days are suspiciously identical.
            var target = (int)(settings.PaymentsPerDay
                * (isWeekend ? 0.55 : 1.0)
                * (0.8 + random.NextDouble() * 0.4));

            for (var i = 0; i < target; i++)
            {
                var at = day.Date
                    .AddHours(HourOfDay(random))
                    .AddMinutes(random.Next(60))
                    .AddSeconds(random.Next(60));

                var stamped = new DateTimeOffset(at, TimeSpan.Zero);

                // The current day is only partly over; don't invent payments from the future.
                if (stamped <= now)
                    times.Add(stamped);
            }
        }

        return times.OrderBy(t => t);
    }

    /// <summary>A rough retail curve: a morning ramp, a lunch bump, an evening peak, a dead night.</summary>
    private static int HourOfDay(Random random)
    {
        int[] weights = [1, 1, 1, 1, 1, 2, 4, 7, 10, 12, 12, 13, 15, 13, 12, 12, 13, 15, 16, 14, 11, 8, 5, 3];
        var roll = random.Next(weights.Sum());

        for (var hour = 0; hour < weights.Length; hour++)
        {
            roll -= weights[hour];
            if (roll < 0)
                return hour;
        }

        return 12;
    }

    /// <summary>
    /// Walks one payment through the lifecycle it would plausibly have had by now. Returns the
    /// settlement job that would exist alongside it, if any.
    /// </summary>
    private static (Payment Payment, SettlementJob? Job) BuildPayment(
        DateTimeOffset createdAt, DateTimeOffset now, Random random)
    {
        var (last4, brand) = Card(random);
        var correlationId = Guid.NewGuid().ToString("N");

        var payment = Payment.Create(
            MerchantId, Amount(random), "USD", last4, brand, Description(random),
            Actor, correlationId, createdAt);

        // The gateway declines roughly one in eight. Terminal: nothing else happens to it.
        if (random.NextDouble() < 0.12)
        {
            payment.Fail(Actor, correlationId, "card declined", createdAt.AddSeconds(1));
            return (payment, null);
        }

        payment.Authorize(Actor, correlationId, createdAt.AddSeconds(1));

        var settled = createdAt.Add(SettlesWithin) < now;

        // Recent authorizations simply haven't been captured yet — and a handful of older ones
        // never will be, which is what an abandoned authorization looks like in a real table.
        if (!settled)
        {
            if (random.NextDouble() < 0.45)
                return (payment, null);
        }
        else if (random.NextDouble() < 0.05)
        {
            return (payment, null);
        }

        var captureCorrelation = Guid.NewGuid().ToString("N");
        var capturedAt = createdAt.AddSeconds(20 + random.Next(600));
        if (capturedAt > now)
            capturedAt = now;

        payment.Capture(Actor, captureCorrelation, capturedAt);

        // Still inside the settlement window: leave it Captured with a live job, so the worker
        // has something to pick up the moment the stack starts — and a reviewer can watch it.
        if (!settled)
        {
            return (payment, NewJob(payment.Id, captureCorrelation, capturedAt, SettlementJobStatus.Pending));
        }

        // A couple of jobs never succeeded. The payment stays Captured and the job sits Dead:
        // money parked for an operator, and the one row that makes the dead-letter story real
        // rather than theoretical.
        if (random.NextDouble() < 0.006)
        {
            var dead = NewJob(payment.Id, captureCorrelation, capturedAt, SettlementJobStatus.Dead);
            dead.AttemptCount = 5;
            dead.LastError = "acquirer timeout (simulated)";
            dead.CompletedAt = capturedAt.AddMinutes(6);
            return (payment, dead);
        }

        var settledAt = capturedAt.AddSeconds(2 + random.Next(120));

        // Some are refunded instead of settling — the customer changed their mind before the
        // money moved, so the job was moot and the worker cancelled it.
        if (random.NextDouble() < 0.04)
        {
            payment.Refund(Actor, Guid.NewGuid().ToString("N"), RefundReason(random), settledAt);

            var cancelled = NewJob(payment.Id, captureCorrelation, capturedAt, SettlementJobStatus.Cancelled);
            cancelled.AttemptCount = 1;
            cancelled.LastError = "payment is Refunded, no longer settleable";
            cancelled.CompletedAt = settledAt;
            return (payment, cancelled);
        }

        payment.MarkSettled("settlement-worker", captureCorrelation, settledAt);

        var succeeded = NewJob(payment.Id, captureCorrelation, capturedAt, SettlementJobStatus.Succeeded);
        succeeded.AttemptCount = random.NextDouble() < 0.2 ? 2 : 1; // the gateway is flaky; some took a retry
        succeeded.CompletedAt = settledAt;

        // And some settled payments are refunded afterwards — the ordinary, real-world refund.
        if (random.NextDouble() < 0.05)
            payment.Refund(Actor, Guid.NewGuid().ToString("N"), RefundReason(random), settledAt.AddHours(random.Next(1, 30)));

        return (payment, succeeded);
    }

    private static SettlementJob NewJob(
        Guid paymentId, string correlationId, DateTimeOffset at, SettlementJobStatus status) =>
        new()
        {
            PaymentId = paymentId,
            Status = status,
            AttemptCount = 0,
            NextAttemptAt = at,
            CorrelationId = correlationId,
            CreatedAt = at,
        };

    /// <summary>Roughly log-normal: lots of small baskets, a few big ones. Flat amounts look fake.</summary>
    private static long Amount(Random random)
    {
        var roll = random.NextDouble();
        return roll switch
        {
            < 0.55 => random.Next(299, 6_000),      // £2.99 – £60
            < 0.85 => random.Next(6_000, 25_000),   // £60 – £250
            < 0.97 => random.Next(25_000, 90_000),  // £250 – £900
            _ => random.Next(90_000, 400_000),      // the occasional big one
        };
    }

    private static (string Last4, string Brand) Card(Random random)
    {
        var roll = random.NextDouble();
        return roll switch
        {
            < 0.52 => ("4242", "visa"),
            < 0.80 => ("4444", "mastercard"),
            < 0.92 => ("0005", "amex"),
            _ => ("1117", "discover"),
        };
    }

    private static string Description(Random random)
    {
        string[] items =
        [
            "headphones", "annual plan", "coffee beans", "desk lamp", "monitor stand", "cable",
            "keyboard", "chair", "mouse mat", "dock", "webcam", "stickers", "notebook", "backpack",
            "microphone", "SSD upgrade", "team seats", "phone case", "laptop sleeve", "extra storage",
        ];

        return $"Order #{random.Next(1000, 9999)} — {items[random.Next(items.Length)]}";
    }

    private static string RefundReason(Random random)
    {
        string[] reasons =
        [
            "customer request", "duplicate order", "item out of stock",
            "damaged on arrival", "changed mind", "wrong size",
        ];

        return reasons[random.Next(reasons.Length)];
    }
}
