using Microsoft.EntityFrameworkCore;
using Payments.Domain;

namespace Payments.Infrastructure;

/// <param name="Search">Full or partial payment id. Merchants paste ids from support tickets.</param>
public sealed record PaymentQuery(
    string MerchantId,
    PaymentStatus? Status,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Search,
    int Page,
    int PageSize);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public interface IPaymentRepository
{
    Task<Payment?> GetAsync(Guid id, string merchantId, CancellationToken ct);

    /// <summary>Merchant-scoped, filtered, newest-first page of payments.</summary>
    Task<PagedResult<Payment>> ListAsync(PaymentQuery query, CancellationToken ct);

    /// <summary>
    /// Unscoped lookup for the settlement worker, which acts on behalf of the platform
    /// rather than a merchant and only ever reaches payments via a job row it claimed.
    /// Kept separate from <see cref="GetAsync"/> so the merchant-scoping rule on every
    /// API-facing query stays impossible to forget.
    /// </summary>
    Task<Payment?> GetForSettlementAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<PaymentTransition>> GetTransitionsAsync(Guid paymentId, CancellationToken ct);
    void Add(Payment payment);

    /// <summary>
    /// Persists the payment and its pending audit transitions in one transaction,
    /// then clears the buffer. All state changes must go through this.
    /// </summary>
    Task SaveAsync(Payment payment, CancellationToken ct);
}

public sealed class PaymentRepository(PaymentsDbContext db) : IPaymentRepository
{
    public Task<Payment?> GetAsync(Guid id, string merchantId, CancellationToken ct) =>
        db.Payments.FirstOrDefaultAsync(p => p.Id == id && p.MerchantId == merchantId, ct);

    public Task<Payment?> GetForSettlementAsync(Guid id, CancellationToken ct) =>
        db.Payments.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<PagedResult<Payment>> ListAsync(PaymentQuery query, CancellationToken ct)
    {
        // Merchant scope is applied first and unconditionally — every other filter is optional,
        // this one never is.
        var payments = db.Payments
            .AsNoTracking()
            .Where(p => p.MerchantId == query.MerchantId);

        if (query.Status is { } status)
            payments = payments.Where(p => p.Status == status);

        if (query.From is { } from)
            payments = payments.Where(p => p.CreatedAt >= from);

        if (query.To is { } to)
            payments = payments.Where(p => p.CreatedAt <= to);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // An exact id is the common case (pasted from a receipt or a support ticket) and
            // hits the primary key. A partial id falls back to a prefix scan, which is fine
            // at this scale but is the query to watch if the table ever grows.
            payments = Guid.TryParse(query.Search, out var id)
                ? payments.Where(p => p.Id == id)
                : payments.Where(p => EF.Functions.ILike(p.Id.ToString(), $"{query.Search}%"));
        }

        // Counted before paging, and on the same filters, so the UI's page count is honest.
        var total = await payments.CountAsync(ct);

        var items = await payments
            .OrderByDescending(p => p.CreatedAt)
            // createdAt alone isn't unique — without a tiebreaker, two payments created in the
            // same tick could swap places between pages and one would never be seen.
            .ThenByDescending(p => p.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        return new PagedResult<Payment>(items, query.Page, query.PageSize, total);
    }

    public async Task<IReadOnlyList<PaymentTransition>> GetTransitionsAsync(Guid paymentId, CancellationToken ct) =>
        await db.PaymentTransitions
            .Where(t => t.PaymentId == paymentId)
            .OrderBy(t => t.OccurredAt)
            .ToListAsync(ct);

    public void Add(Payment payment) => db.Payments.Add(payment);

    public async Task SaveAsync(Payment payment, CancellationToken ct)
    {
        db.PaymentTransitions.AddRange(payment.PendingTransitions);
        await db.SaveChangesAsync(ct);
        payment.ClearPendingTransitions();
    }
}
