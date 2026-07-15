namespace Payments.Domain;

public abstract class DomainException(string errorCode, string message) : Exception(message)
{
    /// <summary>Stable, machine-readable code surfaced in problem+json responses.</summary>
    public string ErrorCode { get; } = errorCode;
}

public sealed class InvalidStateTransitionException(
    Guid paymentId,
    PaymentStatus from,
    PaymentStatus to)
    : DomainException(
        "invalid_state_transition",
        $"Payment {paymentId} cannot transition from {from} to {to}.")
{
    public Guid PaymentId { get; } = paymentId;
    public PaymentStatus From { get; } = from;
    public PaymentStatus To { get; } = to;
}

public sealed class DomainValidationException(string errorCode, string message)
    : DomainException(errorCode, message);
