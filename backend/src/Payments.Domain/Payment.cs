namespace Payments.Domain;

/// <summary>
/// The Payment aggregate. All state changes go through <see cref="Transition"/>,
/// which enforces the state machine and emits an audit record. Uncommitted audit
/// records accumulate in <see cref="PendingTransitions"/> (domain-event style)
/// for the persistence layer to save atomically with the payment itself.
///
/// PCI note: this model only ever holds the card's last four digits and brand.
/// The full PAN / CVV never enter the domain — authorization is delegated to a
/// (simulated) gateway that would hold a token in a real system.
/// </summary>
public sealed class Payment
{
    private readonly List<PaymentTransition> _pendingTransitions = [];

    private Payment() { } // EF Core

    public Guid Id { get; private set; }
    public string MerchantId { get; private set; } = null!;
    public long AmountMinor { get; private set; }
    public string Currency { get; private set; } = null!;
    public string CardLast4 { get; private set; } = null!;
    public string CardBrand { get; private set; } = null!;
    public PaymentStatus Status { get; private set; }
    public string? Description { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<PaymentTransition> PendingTransitions => _pendingTransitions;
    public void ClearPendingTransitions() => _pendingTransitions.Clear();

    public static Payment Create(
        string merchantId,
        long amountMinor,
        string currency,
        string cardLast4,
        string cardBrand,
        string? description,
        string actor,
        string correlationId,
        DateTimeOffset now)
    {
        // Length ceilings match the column widths. Without them the database is the first
        // validator, and its answer to an over-long value is a DbUpdateException — a 500 with
        // no errorCode — instead of the 400 every other bad input gets.
        if (string.IsNullOrWhiteSpace(merchantId) || merchantId.Length > 64)
            throw new DomainValidationException("invalid_merchant", "Merchant id is required and must be at most 64 characters.");
        if (amountMinor <= 0)
            throw new DomainValidationException("invalid_amount", "Amount must be a positive number of minor units.");
        if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetterUpper))
            throw new DomainValidationException("invalid_currency", "Currency must be a 3-letter uppercase ISO 4217 code.");
        if (cardLast4 is not { Length: 4 } || !cardLast4.All(char.IsAsciiDigit))
            throw new DomainValidationException("invalid_card_last4", "Card last4 must be exactly 4 digits.");
        if (string.IsNullOrWhiteSpace(cardBrand) || cardBrand.Length > 32)
            throw new DomainValidationException("invalid_card_brand", "Card brand is required and must be at most 32 characters.");
        if (description is { Length: > 512 })
            throw new DomainValidationException("invalid_description", "Description must be at most 512 characters.");

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            MerchantId = merchantId,
            AmountMinor = amountMinor,
            Currency = currency,
            CardLast4 = cardLast4,
            CardBrand = cardBrand,
            Description = description,
            Status = PaymentStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        payment._pendingTransitions.Add(new PaymentTransition
        {
            PaymentId = payment.Id,
            FromStatus = null,
            ToStatus = PaymentStatus.Pending,
            Actor = actor,
            CorrelationId = correlationId,
            Reason = "created",
            OccurredAt = now,
        });

        return payment;
    }

    public void Authorize(string actor, string correlationId, DateTimeOffset now) =>
        Transition(PaymentStatus.Authorized, actor, correlationId, null, now);

    public void Capture(string actor, string correlationId, DateTimeOffset now) =>
        Transition(PaymentStatus.Captured, actor, correlationId, null, now);

    public void MarkSettled(string actor, string correlationId, DateTimeOffset now) =>
        Transition(PaymentStatus.Settled, actor, correlationId, null, now);

    public void Refund(string actor, string correlationId, string? reason, DateTimeOffset now) =>
        Transition(PaymentStatus.Refunded, actor, correlationId, reason, now);

    public void Fail(string actor, string correlationId, string reason, DateTimeOffset now) =>
        Transition(PaymentStatus.Failed, actor, correlationId, reason, now);

    private void Transition(
        PaymentStatus to,
        string actor,
        string correlationId,
        string? reason,
        DateTimeOffset now)
    {
        if (!PaymentStateMachine.CanTransition(Status, to))
            throw new InvalidStateTransitionException(Id, Status, to);

        var from = Status;
        Status = to;
        UpdatedAt = now;

        _pendingTransitions.Add(new PaymentTransition
        {
            PaymentId = Id,
            FromStatus = from,
            ToStatus = to,
            Actor = actor,
            CorrelationId = correlationId,
            Reason = reason,
            OccurredAt = now,
        });
    }
}
