using Payments.Domain;

namespace Payments.Domain.Tests;

public class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private const string Actor = "merchant:m_test";
    private const string CorrelationId = "corr-123";

    private static Payment NewPayment() =>
        Payment.Create("m_test", 1999, "USD", "4242", "visa", "test payment", Actor, CorrelationId, Now);

    // --- creation ---

    [Fact]
    public void Create_starts_pending_and_emits_creation_audit_record()
    {
        var payment = NewPayment();

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        var t = Assert.Single(payment.PendingTransitions);
        Assert.Null(t.FromStatus);
        Assert.Equal(PaymentStatus.Pending, t.ToStatus);
        Assert.Equal(Actor, t.Actor);
        Assert.Equal(CorrelationId, t.CorrelationId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Create_rejects_non_positive_amounts(long amountMinor)
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            Payment.Create("m_test", amountMinor, "USD", "4242", "visa", null, Actor, CorrelationId, Now));
        Assert.Equal("invalid_amount", ex.ErrorCode);
    }

    [Theory]
    [InlineData("usd")]
    [InlineData("US")]
    [InlineData("USDX")]
    public void Create_rejects_malformed_currency(string currency)
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            Payment.Create("m_test", 100, currency, "4242", "visa", null, Actor, CorrelationId, Now));
        Assert.Equal("invalid_currency", ex.ErrorCode);
    }

    [Theory]
    [InlineData("424")]
    [InlineData("42424")]
    [InlineData("42ab")]
    public void Create_rejects_malformed_last4(string last4)
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            Payment.Create("m_test", 100, "USD", last4, "visa", null, Actor, CorrelationId, Now));
        Assert.Equal("invalid_card_last4", ex.ErrorCode);
    }

    // --- happy path ---

    [Fact]
    public void Full_lifecycle_pending_to_settled_then_refunded()
    {
        var payment = NewPayment();

        payment.Authorize(Actor, CorrelationId, Now);
        payment.Capture(Actor, CorrelationId, Now);
        payment.MarkSettled("settlement-worker", CorrelationId, Now);
        payment.Refund(Actor, CorrelationId, "customer request", Now);

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        // creation + 4 transitions
        Assert.Equal(5, payment.PendingTransitions.Count);
        Assert.Equal("settlement-worker", payment.PendingTransitions[3].Actor);
        Assert.Equal("customer request", payment.PendingTransitions[4].Reason);
    }

    [Fact]
    public void Refund_is_allowed_directly_from_captured()
    {
        var payment = NewPayment();
        payment.Authorize(Actor, CorrelationId, Now);
        payment.Capture(Actor, CorrelationId, Now);

        payment.Refund(Actor, CorrelationId, null, Now);

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
    }

    // --- illegal transitions surface as domain errors, not silent no-ops ---

    [Fact]
    public void Capture_before_authorization_throws()
    {
        var payment = NewPayment();

        var ex = Assert.Throws<InvalidStateTransitionException>(() =>
            payment.Capture(Actor, CorrelationId, Now));

        Assert.Equal(PaymentStatus.Pending, ex.From);
        Assert.Equal(PaymentStatus.Captured, ex.To);
        Assert.Equal(PaymentStatus.Pending, payment.Status); // state unchanged
    }

    [Fact]
    public void Refund_before_capture_throws()
    {
        var payment = NewPayment();
        payment.Authorize(Actor, CorrelationId, Now);

        Assert.Throws<InvalidStateTransitionException>(() =>
            payment.Refund(Actor, CorrelationId, null, Now));
    }

    [Fact]
    public void Terminal_states_reject_all_further_transitions()
    {
        var payment = NewPayment();
        payment.Fail(Actor, CorrelationId, "card declined", Now);

        Assert.Throws<InvalidStateTransitionException>(() => payment.Authorize(Actor, CorrelationId, Now));
        Assert.Throws<InvalidStateTransitionException>(() => payment.Capture(Actor, CorrelationId, Now));
        Assert.Throws<InvalidStateTransitionException>(() => payment.Refund(Actor, CorrelationId, null, Now));
    }

    [Fact]
    public void Failed_transition_records_reason()
    {
        var payment = NewPayment();
        payment.Fail(Actor, CorrelationId, "card declined", Now);

        Assert.Equal("card declined", payment.PendingTransitions[^1].Reason);
    }

    // --- audit plumbing ---

    [Fact]
    public void ClearPendingTransitions_empties_the_buffer_without_touching_state()
    {
        var payment = NewPayment();
        payment.Authorize(Actor, CorrelationId, Now);

        payment.ClearPendingTransitions();

        Assert.Empty(payment.PendingTransitions);
        Assert.Equal(PaymentStatus.Authorized, payment.Status);
    }
}
