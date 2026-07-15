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

/// <summary>Refunds are full-amount only; partial refunds are out of scope (see README).</summary>
public sealed record RefundRequest(string? Reason);

/// <summary>
/// Query string for the list endpoint. Bound with [FromQuery]; every field is optional so
/// the bare URL returns a sensible first page.
/// </summary>
public sealed record ListPaymentsRequest
{
    public string? Status { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    /// <summary>Full or partial payment id.</summary>
    public string? Search { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

/// <summary>
/// Capture carries no client body, and refund carries only a reason — so what gets
/// fingerprinted for idempotency is the command these actions actually represent.
/// The target payment id must be part of it: without it, one key would replay across
/// different payments.
/// </summary>
internal sealed record CaptureCommand(Guid PaymentId);

internal sealed record RefundCommand(Guid PaymentId, string? Reason);

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
