# Payment Processing Platform

A simplified payment processing platform: backend API (.NET 10), React dashboard, and an
asynchronous settlement workflow. Built for the senior engineer technical exercise —
see [docs/PLAN.md](docs/PLAN.md) for the implementation plan.

The brief asks for judgment rather than feature count, so the documentation spends most of
its words on **why** things are the way they are, and on what is deliberately missing.

## Scope — where the depth went

One payment flow, taken seriously end to end: create → authorize → capture → async settle →
refund. The failure modes are treated as the feature — client retries, concurrent duplicates,
crashed workers, refunds racing settlement, jobs that will never succeed — and each one has
tests, several of which were verified by mutation (break the mechanism, watch exactly the
right tests fail). The plan fixed this shape before any code was written: depth over breadth,
one clean vertical slice with a strong operational narrative.

Deliberately **not** built, each with its production path documented in [docs/future-work.md](docs/future-work.md): partial and
multiple captures/refunds, multi-currency, real authentication (a header stub), a real
acquirer (a simulated gateway), broker infrastructure (a transactional outbox instead),
webhooks, automatic dead-letter replay.

Everything in the repo beyond that slice exists to make its depth *observable*, not to widen
it: the demo profile seeds history and drives real traffic so the dashboard and metrics show
true numbers, and the observability profile points a real Grafana/Prometheus/Loki at the
seams to prove they are seams. Both are optional compose profiles; the core neither knows nor
cares whether they're running.

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
- Traces: Tempo, inside Grafana (Explore → Tempo, or click the **TraceID** button on any log
  line) — request waterfalls plus the worker's `settle-payment` spans

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
"seams, not a stack" tradeoff in [docs/tradeoffs.md](docs/tradeoffs.md) means in practice — and this profile is the proof it
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

## Documentation

The written deliverables, one file each:

| Deliverable | Where |
|---|---|
| Setup instructions | This README, [Run it](#run-it) above |
| Architectural overview | [docs/architecture.md](docs/architecture.md) — components, the payment lifecycle, the API surface |
| Tradeoffs made | [docs/tradeoffs.md](docs/tradeoffs.md) — each states what was given up, not just what was chosen |
| Assumptions | [docs/assumptions.md](docs/assumptions.md) |
| Production considerations | [docs/production-considerations.md](docs/production-considerations.md) — including the one known correctness gap, named rather than left to be found |
| Areas for future improvement | [docs/future-work.md](docs/future-work.md) — in the order they'd actually be done |

Supporting documents: [docs/observability.md](docs/observability.md) (logs, metrics, traces,
health — and the reasoning behind each), [docs/testing.md](docs/testing.md) (what's tested,
what's deliberately not, and why), and [docs/PLAN.md](docs/PLAN.md) — the implementation plan
this was built from (untouched since, except correcting its stack line from .NET 8 to 10).
