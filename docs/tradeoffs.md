# Tradeoffs made


**A Postgres table as the queue, not a broker.** The capture handler writes a
`settlement_jobs` row in the *same transaction* as the `Captured` transition, and the worker
claims rows with `FOR UPDATE SKIP LOCKED`. The obvious alternative — commit the capture,
then publish to SQS/Rabbit — is a dual-write: the publish can fail after the capture commits
and the settlement is silently lost. The outbox makes that impossible with zero extra infra,
and gives at-least-once delivery and a free queue-depth metric (`count(*)`).
*Given up:* independent scaling, push semantics, and a latency floor set by `PollInterval`.
*Production:* the outbox stays exactly as it is; a relay publishes from it to a broker and
the worker becomes its own deployment. This design is a step on that path, not a detour.

**Idempotency reserves the key and does the work in one transaction.** The stored response,
the payment's state change, its audit rows and the settlement job all commit together, so a
key can never be consumed without its side effects landing, or vice versa. A concurrent
duplicate blocks on the key's unique index until the first request commits and then replays
it — which means no in-flight state is ever observable and there is no reservation lease to
expire and reclaim.
*Given up:* the gateway round trip happens with a transaction open, and the real cost is
worse than one pinned connection per in-flight payment — **each blocked duplicate holds a
connection too**. Play out the exact scenario idempotency exists for: the acquirer browns out
to 30s latency and a client retries every 5s. One payment is now the original's connection
plus ~6 blocked duplicates; at Npgsql's default pool of 100, roughly fourteen concurrent slow
payments exhaust the pool and *everything* fails, reads and readiness checks included. A
partner brownout becomes a full outage. The alternative (commit the reservation first, then
work) frees the connections but needs lease/expiry logic to recover keys stranded by a crash.
At this scale the simpler, always-consistent option wins; at real throughput the retry-storm
multiplier — not the single pinned connection — is what forces the other design.

**Failures don't burn an idempotency key.** If the work throws, the reservation rolls back
with it, so only responses actually returned are replayable and a client can retry a failed
call with the same key. The alternative — storing 4xx responses — means a transient bug
becomes permanently replayable.

**Replays return status and body, not the full header set.** A replayed create is a 201 with
the identical body and an `Idempotency-Replayed: true` header, but no `Location`. This is
how Stripe replays, and storing whole header sets to reproduce one header didn't earn its
keep.

**Polling with SKIP LOCKED, not LISTEN/NOTIFY.** Simpler, and the row lock is the mutual
exclusion so N workers need no distributed lock. The cost is settlement latency ≈
`PollInterval` and a small constant query load. LISTEN/NOTIFY would cut the latency; at
exercise volumes it's not worth the connection management.

**The claim doubles as a lease.** Claiming stamps `next_attempt_at` into the future and the
poll selects `Pending` *or* `Processing`, so a worker that crashes mid-job has its work
reclaimed when the lease lapses rather than stranding the job forever. This needed no schema
change, and `attempt_count` increments at claim time so a crash loop still counts toward
dead-lettering.

**Modular monolith over services.** Right-sized for one domain slice, and the boundaries are
project references, so the seams a reviewer would ask about are visible without the
distributed-systems tax.

**A simulated gateway.** Keeps PCI scope at zero and makes the failure paths demonstrable
(configurable decline/failure rates, plus a deterministic `-declined` token). No real
acquirer integration is attempted.

**Settlement is idempotent at the acquirer, keyed by the job id.** The lease means a job
*will* sometimes be delivered twice — a worker that stalls past its lease while the acquirer
is processing gets its job reclaimed by another. Settlement is the one call that moves real
money, so `SettleAsync` takes an idempotency key, and the worker passes the settlement job's
id: stable across every attempt and every redelivery of that job. An ambiguous timeout (did it
settle, or not?) is then resolved by the acquirer replaying the original settlement rather
than performing a second one. A fresh key per attempt would make every retry a new settlement,
which is exactly the failure the retry logic exists to survive. Note a broker would **not**
have fixed this — SQS and Rabbit are at-least-once too; only the key is load-bearing.

Transient failures deliberately release the key, so the worker's next attempt genuinely
re-tries; only a completed settlement is replayable. That's the same rule the API's own
idempotency store follows, for the same reason — caching a failure makes it permanent. The
fake gateway implements this contract rather than stubbing it, so the retry and redelivery
paths are tested against something that actually dedupes.

**Business metrics count after commit, and never on replay.** A client retrying a POST five
times moves RED by five and `payments_created_total` by one. Counting inside the work would
have inflated both the retry and the rollback cases, and a counter named `payments_created`
that doesn't mean payments created is worse than no counter.

**`/healthz` checks nothing on purpose.** A liveness probe that depends on Postgres has
Kubernetes kill every API pod during a database blip, turning a recoverable incident into an
outage. Readiness (`/readyz`) is where the dependency check belongs.
