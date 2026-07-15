using Microsoft.EntityFrameworkCore;
using Payments.Domain;

namespace Payments.Infrastructure;

public interface IPaymentRepository
{
    Task<Payment?> GetAsync(Guid id, string merchantId, CancellationToken ct);

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
