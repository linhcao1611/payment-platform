namespace Payments.Domain;

public enum PaymentStatus
{
    Pending,
    Authorized,
    Captured,
    Settled,
    Failed,
    Refunded,
}
