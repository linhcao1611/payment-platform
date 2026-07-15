using Payments.Domain;

namespace Payments.Domain.Tests;

public class PaymentStateMachineTests
{
    // The complete set of legal transitions. Everything else must be rejected.
    private static readonly HashSet<(PaymentStatus, PaymentStatus)> Legal =
    [
        (PaymentStatus.Pending, PaymentStatus.Authorized),
        (PaymentStatus.Pending, PaymentStatus.Failed),
        (PaymentStatus.Authorized, PaymentStatus.Captured),
        (PaymentStatus.Authorized, PaymentStatus.Failed),
        (PaymentStatus.Captured, PaymentStatus.Settled),
        (PaymentStatus.Captured, PaymentStatus.Refunded),
        (PaymentStatus.Settled, PaymentStatus.Refunded),
    ];

    public static TheoryData<PaymentStatus, PaymentStatus, bool> AllPairs()
    {
        var data = new TheoryData<PaymentStatus, PaymentStatus, bool>();
        var statuses = Enum.GetValues<PaymentStatus>();
        foreach (var from in statuses)
            foreach (var to in statuses)
                data.Add(from, to, Legal.Contains((from, to)));
        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Transition_table_is_exhaustive(PaymentStatus from, PaymentStatus to, bool expected) =>
        Assert.Equal(expected, PaymentStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Refunded)]
    public void Failed_and_refunded_are_terminal(PaymentStatus status) =>
        Assert.True(PaymentStateMachine.IsTerminal(status));

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Authorized)]
    [InlineData(PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Settled)]
    public void Live_states_are_not_terminal(PaymentStatus status) =>
        Assert.False(PaymentStateMachine.IsTerminal(status));
}
