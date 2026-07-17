using Prometheus;

namespace Payments.Infrastructure.Observability;

/// <summary>
/// Domain metrics, shared by the API and the worker. RED (rate/errors/duration) comes free
/// from prometheus-net's HTTP middleware; these are the business and queue signals that
/// actually tell an operator whether payments are flowing.
///
/// Two rules held throughout:
///
/// <list type="bullet">
/// <item>Label values are bounded — statuses and outcomes are enums, never a payment id,
/// merchant id or gateway error string. Unbounded labels are how a metrics backend gets
/// taken down by its own instrumentation.</item>
/// <item>Counters are incremented after the work has committed, and never on an idempotent
/// replay, so a client retrying its POST 50 times doesn't show up as 50 captures.</item>
/// </list>
///
/// Counters reset when the process restarts, which is why the queue's standing state
/// (depth, lag, dead backlog) is exposed as gauges sampled from the table rather than
/// inferred from counters.
/// </summary>
public static class PaymentsMetrics
{
    public static readonly Counter Created = Metrics.CreateCounter(
        "payments_created_total", "Payments created (entered Pending).");

    public static readonly Counter Authorized = Metrics.CreateCounter(
        "payments_authorized_total", "Payments authorized by the gateway.");

    public static readonly Counter Failed = Metrics.CreateCounter(
        "payments_failed_total", "Payments that failed authorization.");

    public static readonly Counter Captured = Metrics.CreateCounter(
        "payments_captured_total", "Payments captured and queued for settlement.");

    public static readonly Counter Refunded = Metrics.CreateCounter(
        "payments_refunded_total", "Payments refunded.");

    public static readonly Counter Settled = Metrics.CreateCounter(
        "payments_settled_total", "Payments settled by the worker.");

    /// <summary>Outcome is succeeded | failed | cancelled — three values, forever.</summary>
    public static readonly Counter SettlementAttempts = Metrics.CreateCounter(
        "settlement_attempts_total", "Settlement job attempts by outcome.",
        new CounterConfiguration { LabelNames = ["outcome"] });

    public static readonly Counter SettlementRetries = Metrics.CreateCounter(
        "settlement_retries_total", "Settlement attempts that failed and were rescheduled.");

    public static readonly Counter SettlementDeadLettered = Metrics.CreateCounter(
        "settlement_dead_lettered_total", "Settlement jobs that exhausted their attempts.");

    /// <summary>Jobs waiting to be worked. The single best "is settlement keeping up" signal.</summary>
    public static readonly Gauge QueueDepth = Metrics.CreateGauge(
        "settlement_queue_depth", "Settlement jobs currently pending.");

    /// <summary>
    /// Age of the oldest pending job. Depth alone can't tell a healthy backlog from a stuck
    /// one — a queue of 5 that never drains is worse than a queue of 500 that does.
    /// </summary>
    public static readonly Gauge QueueLagSeconds = Metrics.CreateGauge(
        "settlement_lag_seconds", "Age in seconds of the oldest pending settlement job.");

    /// <summary>
    /// Standing count of dead-lettered jobs. A gauge, not just the counter above, because
    /// this is what you alert on: the counter forgets across restarts, the backlog doesn't.
    /// </summary>
    public static readonly Gauge DeadJobs = Metrics.CreateGauge(
        "settlement_dead_jobs", "Settlement jobs currently in the dead-letter state.");

    /// <summary>Operation is create | capture | refund — the same literal values ExecuteAsync takes.</summary>
    public static readonly Counter IdempotencyReplays = Metrics.CreateCounter(
        "idempotency_replays_total", "Requests served from a stored idempotent response.",
        new CounterConfiguration { LabelNames = ["operation"] });

    /// <summary>
    /// Same key, different payload — always a client bug, so it's worth seeing live rather
    /// than only in the error logs the domain exception also produces.
    /// </summary>
    public static readonly Counter IdempotencyConflicts = Metrics.CreateCounter(
        "idempotency_conflicts_total", "Requests reusing an idempotency key with a different payload.",
        new CounterConfiguration { LabelNames = ["operation"] });
}
