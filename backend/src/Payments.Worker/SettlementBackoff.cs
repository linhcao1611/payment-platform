namespace Payments.Worker;

public static class SettlementBackoff
{
    /// <summary>
    /// Exponential backoff with equal jitter: half the exponential delay, plus a random
    /// half of it. The exponential part backs off a struggling acquirer; the jitter stops
    /// a batch of jobs that failed together from retrying together and re-creating the
    /// spike that failed them.
    ///
    /// <paramref name="jitterSample"/> is a [0,1) roll, passed in rather than drawn here so
    /// the curve is testable without a clock or a seeded RNG. The result lands in
    /// [delay/2, delay).
    /// </summary>
    public static TimeSpan Compute(int attempt, SettlementOptions options, double jitterSample)
    {
        // Guard the shift: attempt is caller-supplied and Math.Pow would overflow to
        // Infinity long before an operator ever set MaxAttempts that high.
        var exponent = Math.Clamp(attempt - 1, 0, 16);
        var raw = options.BaseDelay * Math.Pow(2, exponent);
        var capped = raw < options.MaxDelay ? raw : options.MaxDelay;

        return capped / 2 + capped * (Math.Clamp(jitterSample, 0, 1) / 2);
    }
}
