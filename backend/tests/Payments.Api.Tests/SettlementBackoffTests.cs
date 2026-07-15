using Payments.Worker;

namespace Payments.Api.Tests;

/// <summary>
/// The backoff curve decides how a struggling acquirer gets treated, so its shape is
/// pinned here: it grows, it stays bounded, and it never collapses to zero (which would
/// turn a retry into a hot loop).
/// </summary>
public class SettlementBackoffTests
{
    private static readonly SettlementOptions Options = new()
    {
        BaseDelay = TimeSpan.FromSeconds(2),
        MaxDelay = TimeSpan.FromMinutes(5),
    };

    [Theory]
    [InlineData(1, 2)]    // 2s * 2^0
    [InlineData(2, 4)]    // 2s * 2^1
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    public void Delay_falls_within_the_equal_jitter_band_for_the_attempt(int attempt, double expectedSeconds)
    {
        // Equal jitter: [half the exponential delay, the full delay).
        var min = TimeSpan.FromSeconds(expectedSeconds / 2);
        var max = TimeSpan.FromSeconds(expectedSeconds);

        foreach (var sample in new[] { 0d, 0.25, 0.5, 0.99 })
        {
            var delay = SettlementBackoff.Compute(attempt, Options, sample);
            Assert.InRange(delay, min, max);
        }
    }

    [Fact]
    public void Jitter_spans_the_band_rather_than_pinning_to_one_end()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), SettlementBackoff.Compute(1, Options, 0));
        Assert.Equal(TimeSpan.FromSeconds(1.5), SettlementBackoff.Compute(1, Options, 0.5));
    }

    [Fact]
    public void Delay_is_capped_so_a_long_outage_cannot_push_retries_over_the_horizon()
    {
        foreach (var attempt in new[] { 10, 20, 100, int.MaxValue })
            Assert.InRange(
                SettlementBackoff.Compute(attempt, Options, 0.99),
                Options.MaxDelay / 2,
                Options.MaxDelay);
    }

    [Fact]
    public void Delay_never_reaches_zero()
    {
        // A zero delay would busy-loop the worker against a failing gateway.
        foreach (var attempt in new[] { 0, 1, 5 })
            Assert.True(SettlementBackoff.Compute(attempt, Options, 0) > TimeSpan.Zero);
    }
}
