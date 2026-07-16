# Payment Processing Platform

A simplified payment processing platform: backend API (.NET 10), React dashboard, and an
asynchronous settlement workflow. Built for the senior engineer technical exercise —
see [docs/PLAN.md](docs/PLAN.md) for the implementation plan.

The brief asks for judgment rather than feature count, so this README spends most of its
words on **why** things are the way they are, and on what is deliberately missing.

## Run it

### The whole stack, one command (only Docker needed)

```bash
docker compose --profile demo up --build
```

That's it — no .NET SDK, no Node. It builds the API and the dashboard, waits for Postgres to
be healthy, applies migrations, and starts the settlement worker.

- **Dashboard: http://localhost:5173** ← start here
- API: http://localhost:5080 — opens Swagger, where every endpoint can be tried directly
- Metrics: http://localhost:5080/metrics — Health: `/healthz`, `/readyz`

**What you'll see.** The profile seeds **a week of history — around 400 payments** — into an
empty database. Creating a payment is an API-only operation, so without seed data the first
view would be an empty table with no way to fill it.

The history is shaped rather than random: busy in the working day, dead overnight, lighter at
weekends, with a long tail of small baskets and the occasional large one. That's what makes the
list, the status filters, the date range and 40 pages of paging worth looking at.

A dozen or so are left `Authorized`. Open one, press **Capture**, and watch the status timeline
grow to `Settled` on its own a second later as the worker picks the job up — one click
exercising the state machine, the idempotency key, the transactional outbox, the audit trail
and the async worker together.

Some deliberate details in there: one payment is stuck `Captured` behind a **dead-lettered**
settlement job (five attempts, `acquirer timeout`) — that's the 2am scenario sitting in the
data, queryable and, with the observability profile, alertable. Some jobs are `Cancelled`
because the payment was refunded before settlement ran, and ~20% of successful settlements took
a retry, so `settlement_jobs` looks like a real outbox rather than a clean one.

Every seeded payment goes through the real aggregate, so each has a genuine audit trail with
correlation ids — no rows conjured straight into the table. It runs once and skips a database
that already has payments. Tune with `Demo__Days` and `Demo__PaymentsPerDay`.

The seeder deliberately does **not** touch the metrics: those counters live in the request path,
so Prometheus only ever shows traffic that really happened. Fabricated throughput graphs would
be a lie.

The dashboard is served by nginx, which also proxies `/api`, `/swagger` and `/metrics` to the
API container — so everything is reachable from one origin. Authorization is pinned to always
approve in this profile so a walkthrough doesn't hit a random decline; settlement still fails
~20% of the time, so retries and backoff are visible in `docker compose logs api`. To see the
decline path, use a card token ending in `-declined`, or override the rate:

```bash
FakeGateway__AuthorizeDeclineRate=0.5 docker compose --profile demo up --build
```

Tear down with `docker compose --profile demo down` (add `-v` to drop the database volume).

### Optional: Grafana, Prometheus and Loki

```bash
docker compose --profile demo --profile observability up --build
```

- **Grafana: http://localhost:3000** — opens straight on the *Payments — overview* dashboard,
  no login (anonymous admin; local demo only)
- Prometheus: http://localhost:9090

Everything is provisioned: datasources, the dashboard, the scrape config. The dashboard has
the settlement queue (depth, lag, dead-letter backlog), the payment lifecycle, RED, and a log
panel with a **Search logs** box at the top — paste a correlation id into it and you get one
payment's whole journey, including the worker settling it seconds later on another thread.

**The numbers on those graphs are earned, not backfilled.** The demo profile runs a load
generator (`DemoTraffic__Enabled`, ~10 payments/min) that makes real HTTP requests at the API:
real payments, authorized by the fake gateway, captured, settled by the real worker, some
declined, some refunded, and some retried with the same idempotency key like a flaky client
would. Everything the dashboard shows was measured. Set `DemoTraffic__Enabled=false` and the
graphs go flat, because nothing is happening.

That indirection is the point. Metrics are counters in the *request path*, so they only ever
record what actually happened — which is why the seeder can plant a week of history in Postgres
without moving them a single tick, and why **the time range must be short (15–30 minutes)**: a
seven-day window on a stack that booted five minutes ago is empty, correctly. Backfilling
invented throughput would make every number here unfalsifiable, so the demo generates real work
instead of fake data.

Two things worth watching on the dashboard, because they're the design made visible:

- **Requests/min and payment events/min disagree**, and they should. The generator retries ~15%
  of creates with the same key; the server replays them, so RED counts the request and
  `payments_created_total` doesn't count a payment.
- **`Dead-lettered jobs: 1`** is expected — the seeder plants one stuck payment so the alert has
  something real to fire on.

The point of it being a separate profile: **the application does not change to make any of
this work.** It writes JSON to stdout and exposes `/metrics`, exactly as it does without this
profile. Alloy discovers containers and ships their stdout to Loki; Prometheus scrapes the
endpoint that was already there. No Loki sink, no agent library, no code. That is what the
"seams, not a stack" tradeoff below means in practice — and this profile is the proof it
holds, rather than a claim you'd have to take on trust.

### The dev loop (hot reload)

Prerequisites: .NET 10 SDK, Node 20+, Docker.

```bash
docker compose up -d                          # Postgres only — services in the demo profile stay out
cd backend && dotnet run --project src/Payments.Api    # API on :5080, migrates on startup
cd frontend && npm install && npm run dev              # dashboard on :5173
```

Postgres is on host port **5433** to avoid clashing with a local Postgres on 5432.

> The two paths use the same ports, so run one or the other. If a host dev server and the demo
> stack are both up, `localhost` may resolve to whichever bound IPv6 first — stop one before
> starting the other.

```bash
# Tests (integration tests start their own Postgres via Testcontainers — Docker must be running)
cd backend && dotnet test
```

Outside the demo profile the fake gateway declines ~15% of authorizations and fails ~20% of
settlements, so the failure paths are demonstrable by hand. To make a run deterministic:

```bash
FakeGateway__AuthorizeDeclineRate=0 FakeGateway__SettleFailureRate=0 \
  dotnet run --project src/Payments.Api
```

A card token ending in `-declined` always declines, whatever the rate.

### Inspecting the database

Postgres is published on host port **5433** either way. Credentials are in `docker-compose.yml`
and are local-only, never a real secret:

```
postgresql://payments:payments_dev@localhost:5433/payments
```

Paste that into any client (TablePlus, DBeaver, pgAdmin, DataGrip), or use the container's own
`psql` and install nothing:

```bash
docker compose exec postgres psql -U payments -d payments      # interactive; \dt lists tables, \q quits
docker compose exec -T postgres psql -U payments -d payments -c "SELECT status, count(*) FROM payments GROUP BY 1;"
```

Four tables, and three queries that show what this system is actually about:

```sql
-- The audit trail for one payment. Note the Settled row's actor is settlement-worker,
-- and its correlation_id matches the Captured row's — the worker read that id back off
-- the job it claimed, so the async leg is traceable to the request that caused it.
SELECT from_status, to_status, actor, correlation_id, occurred_at
FROM payment_transitions WHERE payment_id = '<id>' ORDER BY occurred_at;

-- The outbox. Written in the same transaction as the Captured transition, drained by the
-- worker with FOR UPDATE SKIP LOCKED. last_error and attempt_count are the retry story;
-- a Dead row is a payment parked for an operator.
SELECT status, attempt_count, next_attempt_at, last_error, correlation_id FROM settlement_jobs;

-- Stored idempotency responses. response_body is replayed verbatim on a retry;
-- request_hash is what makes a reused key with a different payload a 409.
SELECT operation, key, request_hash, response_status_code FROM idempotency_keys;
```

The data lives in a Docker volume (`payment-platform_pgdata`), so it survives
`docker compose down` but not `down -v` — that's the reset if you want the demo profile to
re-seed a clean database.

## Architecture

```
                   ┌───────────────────┐
                   │  React dashboard  │   list · detail · capture/refund · timeline
                   └─────────┬─────────┘
                             │  /api  (Vite proxy)
                             ▼
   ┌─────────────────────────────────────────────────┐
   │                  Payments.Api                   │
   │  correlation-id → problem+json → controllers    │
   └───────┬─────────────────────────────┬───────────┘
           │                             │
           ▼                             ▼
   ┌───────────────┐            ┌──────────────────┐
   │ Payments      │            │ Payments         │
   │ .Domain       │            │ .Infrastructure  │──────► fake gateway
   │               │            │                  │        (authorize/settle)
   │ state machine │            │ EF Core ·        │
   │ + audit       │            │ idempotency ·    │
   └───────────────┘            │ outbox queue     │
                                └────────┬─────────┘
                                         │
                                         ▼
                    ┌────────────────────────────────────┐
                    │             Postgres               │
                    │  payments · payment_transitions    │
                    │  idempotency_keys · settlement_jobs│
                    └────────────────┬───────────────────┘
                                     │  FOR UPDATE SKIP LOCKED
                                     ▼
                          ┌──────────────────────┐
                          │   Payments.Worker    │  claim → settle → retry/dead-letter
                          │  (BackgroundService, │
                          │   hosted in the API) │
                          └──────────────────────┘
```

| Project | Responsibility |
|---|---|
| `Payments.Domain` | Payment aggregate + state machine. No dependencies. |
| `Payments.Infrastructure` | EF Core persistence, idempotency store, settlement job queue (outbox-style), fake gateway, metric definitions. |
| `Payments.Worker` | Settlement `BackgroundService` — claims jobs, retry w/ backoff, dead-letter. |
| `Payments.Api` | HTTP endpoints, middleware (correlation ID, problem+json errors), composition root. |

Dependencies point one way: `Domain ← Infrastructure ← Api`, and `Worker` sits beside the
API rather than inside it. The worker is hosted in the API process today, but the only
coupling is two lines in `Program.cs` — promoting it to its own deployment is a hosting
change, not a rewrite.

## Payment lifecycle

```
Pending ──► Authorized ──► Captured ──► Settled
   │            │              │           │
   └──► Failed ◄┘              └──► Refunded ◄┘
```

`Failed` is terminal. `Refunded` is terminal and reachable from `Captured` or `Settled` —
refunding after the money has moved is the real-world case, not an edge case.

`PaymentStateMachine` holds the transition table and is the single source of truth. Every
mutation goes through one private `Transition()` method, which is also what appends the
audit record — so an illegal transition and an unaudited transition are both impossible by
construction rather than by review. The domain unit tests assert the full 36-pair matrix.

## API

```
POST   /api/payments                    Idempotency-Key required
GET    /api/payments?status=&from=&to=&search=&page=&pageSize=
GET    /api/payments/{id}
GET    /api/payments/{id}/transitions   audit trail; feeds the UI timeline
POST   /api/payments/{id}/capture       Idempotency-Key required
POST   /api/payments/{id}/refund        Idempotency-Key required
GET    /healthz  /readyz  /metrics
```

Errors are RFC 7807 `application/problem+json` with a stable `errorCode` extension
(`idempotency_conflict`, `invalid_state_transition`, `payment_not_found`, …) so clients can
branch on one field rather than parse prose. Validation is 400, unknown payment is 404,
illegal transition / key conflict / lost concurrency race are 409.

Merchant identity comes from an `X-Merchant-Id` header and **every** query is scoped by it.
Another merchant's payment is a 404, not a 403 — you shouldn't be able to learn that
someone else's payment id exists.

The API is deliberately unversioned (`/api/payments`, not `/api/v1/payments`): with exactly
one consumer, which lives in this repo and deploys with it, a version segment is ceremony.
The moment a first *external* consumer appears, `/v1` goes in — retrofitting a version onto
an unversioned public API is itself the breaking change versioning exists to prevent, so the
cheap time to add it is immediately before publication, and the wrong time is after.

## Tradeoffs made

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

## Assumptions

- **Card data arrives pre-tokenized**, as a client-side tokenization SDK would produce: an
  opaque token plus display-safe metadata. The platform holds `cardLast4` + `cardBrand` +
  the token, and no PAN/CVV field exists anywhere in the domain, the DB or the API. That's
  structural, not a redaction rule someone has to remember.
- **Merchant identity is trusted from a header.** This is an auth stub, not auth.
- **One currency per payment, no FX.** Amounts are integer minor units — never floats.
- **Full-amount capture and refund only.** No partial or multiple captures.
- **Authorization is synchronous at create time** (auth-then-capture); settlement is async.
- **Refunds don't call the gateway.** The state change and audit trail are what's being
  demonstrated; a real refund is a gateway call with its own idempotency and failure modes.
- **Single-region, single database.** No sharding, no read replicas.

## Production considerations

**Authentication and authorization.** Replace the header with API keys or OAuth2
client-credentials per merchant, resolved in middleware into the same merchant scoping the
queries already apply. Add per-merchant rate limiting. The scoping rule is already enforced
at the repository boundary, so this changes *where identity comes from*, not every query.

**Authorization can be orphaned at the acquirer.** This is the sharpest known gap. `create`
calls the gateway *inside* the idempotency transaction. If that transaction rolls back after
the acquirer approved — a commit failure, a crash — the authorization exists on the customer's
card but no payment row does, and because a failure deliberately doesn't burn the key, the
client's retry authorizes a second time. The customer sees two holds. The fix is the same
shape as the settlement one below: pass a gateway idempotency key derived from the client's
`Idempotency-Key`, so the acquirer collapses the retry onto the original authorization. It is
not done here.

**Migrations are a deploy step, not app startup.** They auto-apply here only in Development.
In production this is a migration job gated in the pipeline, so a rolling deploy can't have
two versions racing to migrate.

**Secrets.** The compose password and connection string are in plain config because they're
local-only. Real deployment pulls them from a secrets manager into the environment.

**TLS.** Everything here speaks plain HTTP because everything here is localhost. In
production TLS terminates at the ingress or load balancer and the API keeps listening on
HTTP inside the network — which is why nothing in the code assumes a scheme. Merchant-facing
traffic is TLS-only, HSTS on; whether internal hops also get mTLS is a service-mesh decision,
not an application one.

**Observability wiring.** Metrics are exposed in Prometheus text format at `/metrics` and logs
as JSON on stdout; the `observability` profile above points a real Grafana/Prometheus/Loki at
both without touching a line of application code, which is the property that matters. Traces
use `ActivitySource` with no exporter registered — adding an OTLP exporter is the same kind of
one-config-change. The default stack ships seams, not a monitoring platform, because the seams
are the part that has to be right.

**Idempotency keys are never swept, and neither are settlement jobs.** Both tables grow
forever. Keys need a TTL job (24h is typical) and an index on `created_at`; terminal
settlement jobs (`Succeeded`/`Cancelled`) need archival for the same reason — the queue's
health queries should only ever have to look at the handful of live rows, not a year of
history. The stats query already filters to the live statuses so the metric doesn't become
the queue's biggest customer; archival is what keeps the table itself honest.

**Gateway calls have no timeout budget or circuit breaker.** The in-process fake hides it,
but a real acquirer is a network partner: production wraps `IPaymentGateway` in per-call
timeouts and a circuit breaker (Polly), and the idempotency keys on both sides are what make
those timeouts safe to take.

**`/metrics` is publicly proxied in the demo.** Convenient locally; in production the metrics
endpoint is scraped from inside the network, not exposed through the front door. Likewise the
custom `X-Correlation-Id` would fold into W3C trace context (`traceparent`) — the logs already
carry `@tr` — rather than running two tracing systems side by side.

**Dead-letter runbook.** A dead job leaves the payment `Captured` and visible in
`settlement_dead_jobs`. There is deliberately no automatic replay — an operator should
understand *why* five attempts failed before money moves. Alert on the gauge, not the
counter: counters reset on restart and a real backlog would vanish with one.

**Horizontal scaling.** The API is stateless, and everything that arbitrates a race does it
in the database — the idempotency unique index, `xmin` on payments, `SKIP LOCKED` on the
queue — so N API instances and N workers are safe without any coordination service. The demo
services are the deliberate exception: the seeder serialises itself with an advisory lock so
two instances can't double-seed an empty database, and running N traffic generators simply
multiplies the load.

**PCI scope.** Card data never touching the platform is what keeps scope near zero. In
production that means a tokenization provider or network tokens — the same structural
guarantee (no field to leak) rather than log scrubbing.

## Observability

- **Logs:** Serilog, compact JSON (CLEF) on stdout — no file sinks, no rotation; the container
  runtime owns collection. View them with `docker compose logs -f api`, on the console of
  `dotnet run`, or in Grafana via the observability profile above. Every property is its own
  field (`PaymentId`, `MerchantId`, `CorrelationId`), which is what makes them queryable rather
  than greppable prose, and `@m` carries the rendered message so a human reading them sees
  "Payment abc… captured for merchant acme" rather than an unfilled template. Levels are tuned
  in the `Serilog` config section.
- **Correlation:** `CorrelationIdMiddleware` reads or mints `X-Correlation-Id`, echoes it, and
  pushes it into the logging scope. The id is persisted onto audit rows *and* the settlement
  job, and re-established in the worker's log scope — so grepping one id returns the API
  handler log, the request log, and the worker's settle log for the same payment, across the
  async boundary. That's the point of persisting it rather than passing it in memory:

  ```bash
  docker compose logs api | grep <correlation-id>
  ```
- **Probe noise is dropped, failures aren't.** Successful `/healthz`, `/readyz` and `/metrics`
  requests log at `Verbose`, below the sink's minimum. Left at `Information` they were 77% of
  the local log stream — a k8s probe hits every pod every few seconds forever, and that is both
  an unreadable log and a real ingest bill. Anything that throws or returns 5xx is still an
  `Error` whatever the path, so a failing readiness check — the one probe you must not miss —
  is still there.
- **Metrics:** RED from `UseHttpMetrics()`, plus
  `payments_{created,authorized,failed,captured,refunded,settled}_total`,
  `settlement_attempts_total{outcome}`, `settlement_retries_total`,
  `settlement_dead_lettered_total`, and the queue gauges `settlement_queue_depth`,
  `settlement_lag_seconds`, `settlement_dead_jobs`. Label values are bounded to enums and
  route templates — never ids or gateway error strings, which is how a metrics backend gets
  taken down by its own instrumentation. Depth and lag are both exposed because depth alone
  can't distinguish a healthy backlog from a stuck one.
- **Traces:** an `ActivitySource` span per settlement job, tagged with the payment id and
  correlation id. ASP.NET Core already emits activities for requests; a background loop has
  none unless you make one.

## Testing

- **Domain unit tests** assert the full transition matrix, legal and illegal. Fast, no I/O,
  and the highest-value tests in a payments system.
- **Integration tests** run the real API against a throwaway Postgres (Testcontainers), so
  they don't depend on anyone having run `docker compose up` and never touch data someone is
  looking at. An in-memory provider would prove nothing here: `SKIP LOCKED`, unique-index
  contention and `xmin` concurrency are all Postgres behaviour, and they're the only things
  worth testing at that level. The two concurrency tests fire eight racing requests at one
  idempotency key and assert exactly one did the work.
- **CI** ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs both suites on every
  push: the full backend test run — Testcontainers works on GitHub's hosted runners because
  Docker is already there — plus the frontend's lint and type-checked build.
- **Deliberately skipped:** contract tests (one consumer, and it lives in this repo), load
  tests (they'd earn their keep once the connection-per-in-flight-payment tradeoff above
  starts to bite), and browser E2E (the dashboard is thin enough that the integration tests
  cover the logic worth protecting).

## Areas for future improvement

In the order I'd actually do them:

1. **Gateway-side idempotency key on authorization** — closes the orphaned-authorization
   window described above, the same way settlement is already closed. The only known
   correctness gap, and the reason it's first.
2. **Split `Refund` into `Void` and `Refund`.** Reversing before settlement is an
   auth-reversal — the money never moved; after settlement it's a true refund — the money
   moves back. Today both are `Refunded`, and the only trace of the difference is the worker
   cancelling the moot settlement job. They're different gateway calls with different failure
   modes, so the state machine should name them. Related: **authorizations should expire** —
   real card auths die in about seven days, and abandoned `Authorized` payments currently sit
   forever with no sweep to an expired state.
3. **Webhooks.** The platform records everything and tells no one — a merchant discovers
   settlement by polling. The fix is the settlement outbox pattern a second time: an event row
   written in the same transaction as the state change, a dispatcher with retries, backoff and
   dead-lettering, idempotent delivery keyed by event id. That `settlement_jobs` is a
   specialization of a pattern that generalizes this cleanly is the strongest evidence the
   original choice was right.
4. **Retention: idempotency-key TTL sweep and settlement-job archival** — both tables grow
   unbounded.
5. **Partial and multiple captures/refunds** — the real shape of the domain; the aggregate
   would carry amounts per operation rather than a single status.
6. **Reconciliation against the acquirer** — a daily job comparing settled payments to the
   gateway's report. In a real system this, not the retry logic, is what catches the money
   that actually went missing.
7. **LISTEN/NOTIFY or a broker relay** — removes the polling latency floor once it matters.
8. **Outbox → broker migration** — when settlement needs to scale independently or fan out
   to other consumers.
