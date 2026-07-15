namespace Payments.Domain;

/// <summary>
/// Append-only audit record of a payment state change: who/what moved the
/// payment, when, and from which state to which. FromStatus is null for the
/// initial creation event.
/// </summary>
public sealed record PaymentTransition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid PaymentId { get; init; }
    public required PaymentStatus? FromStatus { get; init; }
    public required PaymentStatus ToStatus { get; init; }

    /// <summary>Who caused the change, e.g. "merchant:m_123" or "settlement-worker".</summary>
    public required string Actor { get; init; }

    public required string CorrelationId { get; init; }
    public string? Reason { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}
