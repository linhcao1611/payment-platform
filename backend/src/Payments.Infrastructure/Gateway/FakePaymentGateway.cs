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
    Task<GatewayResult> SettleAsync(Guid paymentId, CancellationToken ct);
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

    public async Task<GatewayResult> SettleAsync(Guid paymentId, CancellationToken ct)
    {
        await SimulateLatency(ct);

        return Random.Shared.NextDouble() < _options.SettleFailureRate
            ? new GatewayResult(false, "acquirer timeout (simulated)")
            : new GatewayResult(true, null);
    }

    private Task SimulateLatency(CancellationToken ct) =>
        Task.Delay(Random.Shared.Next(_options.MinLatencyMs, _options.MaxLatencyMs), ct);
}
