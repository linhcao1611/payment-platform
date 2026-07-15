namespace Payments.Worker;

public sealed class SettlementOptions
{
    public const string SectionName = "Settlement";

    /// <summary>
    /// Lets the API host run without the worker — useful for integration tests that want
    /// to drive settlement deterministically, and the seam along which the worker would
    /// become its own deployment.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long to sleep when a poll finds nothing. Sets the idle settlement latency.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public int BatchSize { get; set; } = 10;

    /// <summary>Deliveries allowed before a job is dead-lettered for an operator.</summary>
    public int MaxAttempts { get; set; } = 5;

    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a claimed job stays invisible to other workers. Must comfortably exceed the
    /// slowest expected gateway call: too short and a slow job gets processed twice
    /// concurrently; too long and a crashed worker's jobs sit idle before reclaim.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);
}
