using Microsoft.EntityFrameworkCore;
using Payments.Infrastructure.Entities;

namespace Payments.Infrastructure;

/// <summary>
/// The settlement queue: a Postgres table standing in for a broker.
///
/// <see cref="Enqueue"/> deliberately does not save — the job joins the same change
/// tracker as the payment's Captured transition, so both land in one transaction. A
/// capture can never commit without its settlement job, and a job can never reference a
/// capture that rolled back. That's at-least-once delivery with no broker in the picture.
/// </summary>
public interface ISettlementQueue
{
    void Enqueue(Guid paymentId, string correlationId, DateTimeOffset now);

    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> due jobs and leases them for
    /// <paramref name="leaseDuration"/>. Returns the claimed rows, tracked for mutation.
    /// </summary>
    Task<IReadOnlyList<SettlementJob>> ClaimDueAsync(
        int batchSize, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);
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

    /// <summary>
    /// Claim-by-update in one round trip. SKIP LOCKED lets N workers share the table
    /// without blocking each other or handing the same job out twice — the row lock is
    /// the mutual exclusion, so no distributed lock is needed.
    ///
    /// The claim also acts as a lease: it stamps <c>next_attempt_at</c> into the future
    /// and leaves the row visible to the same query as a Processing row. A worker that
    /// crashes mid-job therefore has its work reclaimed once the lease lapses, rather
    /// than stranding the job forever. That's why this selects Processing as well as
    /// Pending, and why the attempt count increments here (at delivery) rather than on
    /// failure — a crash loop still counts toward the dead-letter threshold.
    ///
    /// Redelivery is the tradeoff: the worker must treat every job as at-least-once.
    /// </summary>
    public async Task<IReadOnlyList<SettlementJob>> ClaimDueAsync(
        int batchSize, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct)
    {
        var leaseUntil = now + leaseDuration;

        return await db.SettlementJobs
            .FromSql(
                $"""
                 UPDATE settlement_jobs
                    SET status = 'Processing',
                        attempt_count = attempt_count + 1,
                        next_attempt_at = {leaseUntil}
                  WHERE id IN (
                        SELECT id
                          FROM settlement_jobs
                         WHERE status IN ('Pending', 'Processing')
                           AND next_attempt_at <= {now}
                         ORDER BY next_attempt_at
                         LIMIT {batchSize}
                         FOR UPDATE SKIP LOCKED
                  )
                 RETURNING *
                 """)
            .ToListAsync(ct);
    }

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
