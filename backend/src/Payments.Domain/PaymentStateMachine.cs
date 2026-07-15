namespace Payments.Domain;

/// <summary>
/// The single source of truth for legal payment state transitions.
///
///   Pending → Authorized → Captured → Settled
///
/// with Failed (from Pending/Authorized) and Refunded (from Captured/Settled)
/// as terminal branch states.
/// </summary>
public static class PaymentStateMachine
{
    private static readonly IReadOnlyDictionary<PaymentStatus, PaymentStatus[]> Transitions =
        new Dictionary<PaymentStatus, PaymentStatus[]>
        {
            [PaymentStatus.Pending] = [PaymentStatus.Authorized, PaymentStatus.Failed],
            [PaymentStatus.Authorized] = [PaymentStatus.Captured, PaymentStatus.Failed],
            [PaymentStatus.Captured] = [PaymentStatus.Settled, PaymentStatus.Refunded],
            [PaymentStatus.Settled] = [PaymentStatus.Refunded],
            [PaymentStatus.Failed] = [],
            [PaymentStatus.Refunded] = [],
        };

    public static bool CanTransition(PaymentStatus from, PaymentStatus to) =>
        Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static bool IsTerminal(PaymentStatus status) =>
        Transitions.TryGetValue(status, out var allowed) && allowed.Length == 0;
}
