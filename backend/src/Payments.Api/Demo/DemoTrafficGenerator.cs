using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace Payments.Api.Demo;

public sealed class DemoTrafficOptions
{
    public const string SectionName = "DemoTraffic";

    /// <summary>Off unless asked for. The compose demo profile turns it on; tests never do.</summary>
    public bool Enabled { get; set; }

    /// <summary>New payments per minute. Enough to make the graphs move, not enough to bury the list.</summary>
    public double PaymentsPerMinute { get; set; } = 10;

    /// <summary>Share of authorized payments that get captured.</summary>
    public double CaptureRate { get; set; } = 0.8;

    /// <summary>Share of captured payments that get refunded.</summary>
    public double RefundRate { get; set; } = 0.06;

    /// <summary>
    /// Share of payments deliberately sent with a card token the fake gateway always declines.
    /// The demo profile pins the gateway's *random* decline rate to 0 so a reviewer clicking
    /// Capture never hits a surprise, which would otherwise leave the failure path invisible on
    /// every dashboard. Forcing declines here keeps that path real without making manual
    /// clicking a lottery.
    /// </summary>
    public double DeclineRate { get; set; } = 0.1;

    /// <summary>
    /// Share of requests the "client" retries with the same idempotency key, as a flaky client
    /// would. Makes the divergence between RED and the business counters visible on the
    /// dashboard rather than merely claimed.
    /// </summary>
    public double ReplayRate { get; set; } = 0.15;
}

/// <summary>
/// Drives real traffic at the API so the dashboards have something true to show.
///
/// This is the honest answer to "why are the metrics empty?". Metrics are counters in the
/// request path: they record what actually happened, and seeded history doesn't move them
/// because seeded history never happened. Rather than backfill invented numbers — which would
/// make every graph unfalsifiable — this makes real requests over real HTTP through the real
/// pipeline: middleware, correlation ids, idempotency, the state machine, the outbox, the
/// worker. Everything the dashboard shows is then genuinely earned.
///
/// It is a demo aid, not production code, and is gated behind <c>DemoTraffic:Enabled</c>.
/// </summary>
public sealed class DemoTrafficGenerator(
    IServer server,
    IHostApplicationLifetime lifetime,
    IOptions<DemoTrafficOptions> options,
    DemoTrafficControl control,
    ILogger<DemoTrafficGenerator> logger) : BackgroundService
{
    private const string MerchantId = "acme";
    private static readonly Random Random = new();

    /// <summary>
    /// How often a paused loop re-checks <see cref="DemoTrafficControl.IsPaused"/>. Short
    /// enough that Resume from the dashboard feels immediate, without a signal/event primitive.
    /// </summary>
    private static readonly TimeSpan PausedPollInterval = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled || settings.PaymentsPerMinute <= 0)
            return;

        // Hosted services start before Kestrel is listening, so calling ourselves immediately
        // would just connection-refuse.
        await WaitForStartupAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested)
            return;

        var baseAddress = ResolveBaseAddress();
        if (baseAddress is null)
        {
            logger.LogWarning("Demo traffic generator could not resolve the server address; not starting.");
            return;
        }

        using var http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Add("X-Merchant-Id", MerchantId);

        var interval = TimeSpan.FromSeconds(60d / settings.PaymentsPerMinute);
        logger.LogInformation(
            "Demo traffic generator started against {BaseAddress}: ~{Rate}/min", baseAddress, settings.PaymentsPerMinute);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (control.IsPaused)
            {
                try
                {
                    await Task.Delay(PausedPollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            try
            {
                await DriveOnePaymentAsync(http, settings, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // A demo aid must never take the process down or spam the log it exists to fill.
                logger.LogDebug(e, "Demo traffic generator request failed.");
            }

            // Jittered, so the graphs look like traffic rather than a metronome.
            var delay = interval * (0.5 + Random.NextDouble());
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task DriveOnePaymentAsync(HttpClient http, DemoTrafficOptions settings, CancellationToken ct)
    {
        var createKey = Guid.NewGuid().ToString();
        var declines = Random.NextDouble() < settings.DeclineRate;
        var body = new
        {
            amountMinor = Amount(),
            currency = "USD",
            // The gateway's deterministic hook: this token always declines.
            cardToken = declines ? "tok_visa-declined" : "tok_visa",
            cardLast4 = declines ? "0002" : "4242",
            cardBrand = "visa",
            description = Description(),
        };

        using var response = await PostAsync(http, "/api/payments", body, createKey, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug("Demo traffic: create returned {StatusCode}", response.StatusCode);
            return;
        }

        var payment = await response.Content.ReadFromJsonAsync<CreatedPayment>(cancellationToken: ct);
        if (payment is null)
            return;

        // A real client retries. The *same* key, so the server replays and no second payment
        // exists — which is exactly why "Requests / min" and "Payment events / min" disagree on
        // the dashboard, and now they disagree for a real reason rather than a claimed one.
        if (Random.NextDouble() < settings.ReplayRate)
            (await PostAsync(http, "/api/payments", body, createKey, ct)).Dispose();

        if (payment.Status is not "Authorized" || Random.NextDouble() >= settings.CaptureRate)
            return;

        using var captured = await PostAsync(http, $"/api/payments/{payment.Id}/capture", null, Guid.NewGuid().ToString(), ct);
        if (!captured.IsSuccessStatusCode)
            return;

        if (Random.NextDouble() >= settings.RefundRate)
            return;

        // Refund some of them. Whether this beats the worker to the payment or not is a genuine
        // race, and both outcomes are legal — that's the point of the state machine.
        (await PostAsync(
            http, $"/api/payments/{payment.Id}/refund",
            new { reason = "customer request" }, Guid.NewGuid().ToString(), ct)).Dispose();
    }

    /// <summary>
    /// Every mutating endpoint requires an Idempotency-Key. Routing all posts through here is
    /// deliberate: the first version built the key and forgot to send it, so every request came
    /// back 400 and the generator sat there silently producing nothing.
    /// </summary>
    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient http, string url, object? body, string idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        if (body is not null)
            request.Content = JsonContent.Create(body);

        return await http.SendAsync(request, ct);
    }

    private async Task WaitForStartupAsync(CancellationToken ct)
    {
        var started = new TaskCompletionSource();
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var cancellation = ct.Register(() => started.TrySetCanceled(ct));

        try
        {
            await started.Task;
        }
        catch (OperationCanceledException)
        {
            // shutting down before we ever started
        }
    }

    /// <summary>
    /// Asks the server where it actually ended up listening, rather than assuming a port. The
    /// demo runs on 8080 in a container and 5080 on a developer's machine.
    /// </summary>
    private Uri? ResolveBaseAddress()
    {
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (address is null)
            return null;

        // Wildcard binds aren't dialable; loop back to ourselves.
        var dialable = address
            .Replace("://[::]", "://localhost", StringComparison.Ordinal)
            .Replace("://+", "://localhost", StringComparison.Ordinal)
            .Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal);

        return Uri.TryCreate(dialable, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static long Amount() =>
        Random.NextDouble() switch
        {
            < 0.6 => Random.Next(299, 6_000),
            < 0.9 => Random.Next(6_000, 25_000),
            _ => Random.Next(25_000, 120_000),
        };

    private static string Description()
    {
        string[] items = ["headphones", "coffee beans", "desk lamp", "cable", "keyboard", "webcam", "notebook", "backpack"];
        return $"Order #{Random.Next(1000, 9999)} — {items[Random.Next(items.Length)]}";
    }

    private sealed record CreatedPayment(Guid Id, string Status);
}
