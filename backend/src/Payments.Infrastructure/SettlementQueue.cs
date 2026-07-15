using Payments.Infrastructure.Entities;

namespace Payments.Infrastructure;

/// <summary>
/// Outbox-style enqueue for settlement.
///
/// Deliberately does not save: the job joins the same change tracker as the payment's
/// Captured transition, so both land in one transaction. A capture can never commit
/// without its settlement job, and a job can never reference a capture that rolled
/// back — at-least-once delivery with no broker in the picture. The worker (slice 5)
/// claims these rows with FOR UPDATE SKIP LOCKED.
/// </summary>
public interface ISettlementQueue
{
    void Enqueue(Guid paymentId, string correlationId, DateTimeOffset now);
}

public sealed class SettlementQueue(PaymentsDbContext db) : ISettlementQueue
{
    public void Enqueue(Guid paymentId, string correlationId, DateTimeOffset now) =>
        db.SettlementJobs.Add(new SettlementJob
        {
            PaymentId = paymentId,
            Status = SettlementJobStatus.Pending,
            AttemptCount = 0,
            // Due immediately; the worker's backoff pushes this out on failure.
            NextAttemptAt = now,
            CorrelationId = correlationId,
            CreatedAt = now,
        });
}
