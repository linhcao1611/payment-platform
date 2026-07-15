using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Payments.Infrastructure.Gateway;

public sealed record GatewayResult(bool Approved, string? DeclineReason);

/// <summary>
/// Stand-in for an acquirer / PSP integration. Takes an opaque card token —
/// the platform never sees a PAN. Failure rate and latency are configurable so
/// the Failed path and worker retries can be exercised in a demo.
/// </summary>
public interface IPaymentGateway
{
    Task<GatewayResult> AuthorizeAsync(string cardToken, long amountMinor, string currency, CancellationToken ct);

    /// <summary>
    /// Settles a captured payment.
    ///
    /// <paramref name="idempotencyKey"/> must be stable across every retry and redelivery of
    /// the same logical settlement — the settlement job's id, not a fresh value per call.
    /// Delivery is at-least-once and the acquirer call is the one step that moves real money,
    /// so this key is what stops an ambiguous timeout (did it settle? did it not?) from being
    /// resolved by settling twice. Retrying with a fresh key would make every retry a new
    /// settlement, which is precisely the failure the retry logic exists to survive.
    /// </summary>
    Task<GatewayResult> SettleAsync(Guid paymentId, string idempotencyKey, CancellationToken ct);
}

public sealed class FakeGatewayOptions
{
    public const string SectionName = "FakeGateway";

    /// <summary>0.0–1.0 chance an authorization is declined.</summary>
    public double AuthorizeDeclineRate { get; set; } = 0.15;

    /// <summary>0.0–1.0 chance a settlement attempt fails transiently (exercises worker retries).</summary>
    public double SettleFailureRate { get; set; } = 0.2;

    public int MinLatencyMs { get; set; } = 20;
    public int MaxLatencyMs { get; set; } = 120;
}

public sealed class FakePaymentGateway(IOptions<FakeGatewayOptions> options) : IPaymentGateway
{
    private readonly FakeGatewayOptions _options = options.Value;

    /// <summary>
    /// Settlements the "acquirer" has completed, keyed by idempotency key. A real gateway
    /// keeps this server-side; the fake honours the same contract so the worker's retry and
    /// redelivery paths are exercised against something that actually dedupes, rather than
    /// against a stub that would make the key look load-bearing without proving it is.
    ///
    /// The value is a <see cref="Lazy{T}"/> of the in-flight call, so two workers racing on
    /// one key await the same acquirer call instead of both making one.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<GatewayResult>>> _settlements = new();

    /// <summary>
    /// Approximate-FIFO eviction of remembered settlements. This is a singleton and the demo
    /// traffic generator settles ~14k payments/day, so without a bound the dictionary grows
    /// forever — the in-memory twin of the "idempotency keys are never swept" gap the README
    /// owns for the database. A real gateway holds these server-side with a TTL (24h is
    /// typical); the fake just needs to outlive any plausible redelivery window, and the
    /// worker's lease is one minute.
    /// </summary>
    private readonly ConcurrentQueue<string> _settlementOrder = new();

    private const int MaxRememberedSettlements = 10_000;

    private int _acquirerCalls;

    /// <summary>
    /// How many times the "acquirer" was actually contacted for a settlement. Test surface:
    /// approvals alone can't distinguish a replay from a second charge.
    /// </summary>
    public int AcquirerSettlementCalls => Volatile.Read(ref _acquirerCalls);

    public async Task<GatewayResult> AuthorizeAsync(string cardToken, long amountMinor, string currency, CancellationToken ct)
    {
        await SimulateLatency(ct);

        // Deterministic hook for demos/tests: tokens ending in "-declined" always decline.
        if (cardToken.EndsWith("-declined", StringComparison.OrdinalIgnoreCase))
            return new GatewayResult(false, "card declined");

        return Random.Shared.NextDouble() < _options.AuthorizeDeclineRate
            ? new GatewayResult(false, "card declined")
            : new GatewayResult(true, null);
    }

    public async Task<GatewayResult> SettleAsync(Guid paymentId, string idempotencyKey, CancellationToken ct)
    {
        var attempt = _settlements.GetOrAdd(
            idempotencyKey,
            _ => new Lazy<Task<GatewayResult>>(
                () => ContactAcquirerAsync(ct), LazyThreadSafetyMode.ExecutionAndPublication));

        GatewayResult result;
        try
        {
            result = await attempt.Value;
        }
        catch
        {
            // A call that blew up settled nothing, so it must not be the cached answer forever.
            _settlements.TryRemove(idempotencyKey, out _);
            throw;
        }

        // Only a completed settlement is replayable. A transient failure deliberately releases
        // the key so the worker's next attempt genuinely re-tries — the same rule the API's own
        // idempotency store follows, for the same reason: caching a failure makes it permanent.
        if (!result.Approved)
        {
            _settlements.TryRemove(idempotencyKey, out _);
        }
        else
        {
            // Replays enqueue duplicates, which makes the FIFO approximate — acceptable for a
            // fake whose only requirement is "doesn't grow forever".
            _settlementOrder.Enqueue(idempotencyKey);
            while (_settlements.Count > MaxRememberedSettlements && _settlementOrder.TryDequeue(out var oldest))
                _settlements.TryRemove(oldest, out _);
        }

        return result;
    }

    private async Task<GatewayResult> ContactAcquirerAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _acquirerCalls);
        await SimulateLatency(ct);

        return Random.Shared.NextDouble() < _options.SettleFailureRate
            ? new GatewayResult(false, "acquirer timeout (simulated)")
            : new GatewayResult(true, null);
    }

    private Task SimulateLatency(CancellationToken ct) =>
        Task.Delay(Random.Shared.Next(_options.MinLatencyMs, _options.MaxLatencyMs), ct);
}
