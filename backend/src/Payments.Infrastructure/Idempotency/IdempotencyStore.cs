using Microsoft.EntityFrameworkCore;
using Npgsql;
using Payments.Domain;
using Payments.Infrastructure.Entities;

namespace Payments.Infrastructure.Idempotency;

/// <summary>The response to store under an idempotency key and replay on retry.</summary>
public sealed record IdempotentOutcome(int StatusCode, string ResponseBody);

/// <summary>
/// Result of an idempotent execution. <paramref name="Replayed"/> is true when the
/// response came from storage instead of from running the work.
/// </summary>
public sealed record IdempotentResult(int StatusCode, string ResponseBody, bool Replayed);

/// <summary>Same key, different request payload — the client has a bug, so say so loudly.</summary>
public sealed class IdempotencyConflictException(string key)
    : DomainException(
        "idempotency_conflict",
        $"Idempotency key '{key}' was already used for a different request payload.");

public interface IIdempotencyStore
{
    /// <summary>
    /// Runs <paramref name="work"/> at most once per (merchant, operation, key), replaying
    /// the stored response on any retry.
    /// </summary>
    Task<IdempotentResult> ExecuteAsync(
        string merchantId,
        string operation,
        string key,
        string requestHash,
        Func<CancellationToken, Task<IdempotentOutcome>> work,
        CancellationToken ct);
}

public sealed class IdempotencyStore(PaymentsDbContext db, TimeProvider clock) : IIdempotencyStore
{
    private const string UniqueViolation = "23505";

    public async Task<IdempotentResult> ExecuteAsync(
        string merchantId,
        string operation,
        string key,
        string requestHash,
        Func<CancellationToken, Task<IdempotentOutcome>> work,
        CancellationToken ct)
    {
        if (await FindAsync(merchantId, operation, key, ct) is { } stored)
            return Replay(stored, requestHash, key);

        // Reserve the key and run the work in a single transaction. The stored response,
        // the payment's new state, its audit rows and any settlement job commit together,
        // so a key can never be consumed without its side effects landing (or vice versa)
        // — no reservation lease to expire, no half-applied retry to reconcile.
        // Tradeoff: the gateway round trip happens with a transaction open. Fine at this
        // scale; under real acquirer latency it would pin a pooled connection per in-flight
        // payment. See README.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var reservation = new IdempotencyKey
        {
            Key = key,
            MerchantId = merchantId,
            Operation = operation,
            RequestHash = requestHash,
            ResponseStatusCode = 0,
            ResponseBody = string.Empty,
            CreatedAt = clock.GetUtcNow(),
        };
        db.IdempotencyKeys.Add(reservation);

        try
        {
            // A concurrent request with the same key blocks on this insert until we commit
            // or roll back, then takes the replay path below. The half-written reservation
            // is never visible to it, so a duplicate can't double-charge and can't observe
            // an in-progress state either.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            db.Entry(reservation).State = EntityState.Detached;
            await tx.RollbackAsync(ct);

            var winner = await FindAsync(merchantId, operation, key, ct)
                ?? throw new InvalidOperationException(
                    $"Idempotency key '{key}' collided on insert but no stored record was found.");

            return Replay(winner, requestHash, key);
        }

        // If the work throws, disposing the transaction rolls back the reservation too:
        // failures don't burn the key, so the client can retry it as-is. Only responses
        // we actually returned are replayable.
        var outcome = await work(ct);

        reservation.ResponseStatusCode = outcome.StatusCode;
        reservation.ResponseBody = outcome.ResponseBody;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new IdempotentResult(outcome.StatusCode, outcome.ResponseBody, Replayed: false);
    }

    private Task<IdempotencyKey?> FindAsync(
        string merchantId, string operation, string key, CancellationToken ct) =>
        db.IdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(
                k => k.MerchantId == merchantId && k.Operation == operation && k.Key == key, ct);

    private static IdempotentResult Replay(IdempotencyKey stored, string requestHash, string key) =>
        stored.RequestHash == requestHash
            ? new IdempotentResult(stored.ResponseStatusCode, stored.ResponseBody, Replayed: true)
            : throw new IdempotencyConflictException(key);
}
