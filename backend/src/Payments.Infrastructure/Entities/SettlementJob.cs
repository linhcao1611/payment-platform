namespace Payments.Infrastructure.Entities;

public enum SettlementJobStatus
{
    Pending,
    Processing,
    Succeeded,
    Dead,

    /// <summary>
    /// The job became moot before it ran — the payment was refunded between capture and
    /// settlement, so there is nothing to settle. Distinct from Dead: nothing failed and
    /// no operator needs to look at it. Free to add because status is stored as text.
    /// </summary>
    Cancelled,
}

/// <summary>
/// Outbox-style settlement job. Enqueued in the same transaction as the
/// Captured state change; the worker claims rows with FOR UPDATE SKIP LOCKED,
/// retries with exponential backoff, and dead-letters after MaxAttempts.
/// </summary>
public sealed class SettlementJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid PaymentId { get; init; }
    public SettlementJobStatus Status { get; set; } = SettlementJobStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>Correlation id carried over from the capture request, so the
    /// async leg of the payment's journey is traceable end to end.</summary>
    public required string CorrelationId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
}
