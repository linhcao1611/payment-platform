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

    /// <summary>Samples the queue's standing state for the gauges. Cheap enough to run per poll.</summary>
    Task<SettlementQueueStats> GetStatsAsync(DateTimeOffset now, CancellationToken ct);
}

/// <param name="OldestPendingSeconds">Age of the oldest pending job; 0 when the queue is empty.</param>
public sealed record SettlementQueueStats(int PendingCount, int DeadCount, double OldestPendingSeconds);

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

    /// <summary>
    /// Reads only the live statuses. Terminal rows (Succeeded/Cancelled) accumulate forever —
    /// thousands a day under real traffic — and this runs on every worker poll, so grouping
    /// over the whole table would make the query O(all jobs ever): the queue-depth metric
    /// slowly becoming the queue's biggest customer is a classic way for observability to
    /// become the outage. Filtered to Pending and Dead, the (status, next_attempt_at) index
    /// covers it and the row count is bounded by the backlog, not by history.
    /// </summary>
    public async Task<SettlementQueueStats> GetStatsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var counts = await db.SettlementJobs
            .Where(j => j.Status == SettlementJobStatus.Pending || j.Status == SettlementJobStatus.Dead)
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var oldestPendingAt = await db.SettlementJobs
            .Where(j => j.Status == SettlementJobStatus.Pending)
            .MinAsync(j => (DateTimeOffset?)j.CreatedAt, ct);

        return new SettlementQueueStats(
            PendingCount: counts.FirstOrDefault(c => c.Status == SettlementJobStatus.Pending)?.Count ?? 0,
            DeadCount: counts.FirstOrDefault(c => c.Status == SettlementJobStatus.Dead)?.Count ?? 0,
            OldestPendingSeconds: oldestPendingAt is { } oldest
                ? Math.Max(0, (now - oldest).TotalSeconds)
                : 0);
    }
}
