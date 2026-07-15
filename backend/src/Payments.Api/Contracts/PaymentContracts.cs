using Payments.Domain;

namespace Payments.Api.Contracts;

/// <summary>
/// Card data arrives pre-tokenized (as a real client-side tokenization SDK
/// would produce): an opaque token for the gateway plus display-safe metadata.
/// The API never receives or stores a PAN or CVV.
/// </summary>
public sealed record CreatePaymentRequest(
    long AmountMinor,
    string Currency,
    string CardToken,
    string CardLast4,
    string CardBrand,
    string? Description);

public sealed record PaymentResponse(
    Guid Id,
    string MerchantId,
    long AmountMinor,
    string Currency,
    string CardLast4,
    string CardBrand,
    string Status,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static PaymentResponse From(Payment p) => new(
        p.Id, p.MerchantId, p.AmountMinor, p.Currency, p.CardLast4, p.CardBrand,
        p.Status.ToString(), p.Description, p.CreatedAt, p.UpdatedAt);
}

public sealed record TransitionResponse(
    string? FromStatus,
    string ToStatus,
    string Actor,
    string CorrelationId,
    string? Reason,
    DateTimeOffset OccurredAt)
{
    public static TransitionResponse From(PaymentTransition t) => new(
        t.FromStatus?.ToString(), t.ToStatus.ToString(), t.Actor,
        t.CorrelationId, t.Reason, t.OccurredAt);
}
