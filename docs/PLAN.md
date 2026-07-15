# Payment Platform Exercise — Implementation Plan

**Stack:** .NET 8 (ASP.NET Core Web API), React + TypeScript (Vite), Postgres via docker-compose, in-process background worker.
**Timebox:** ~1 day. Depth over breadth — one clean vertical slice with a strong operational narrative.

---

## 1. Guiding thesis

The brief is explicit: they're grading senior judgment, not features. The plan optimizes for three things reviewers will actually look at:

1. **Correctness under retries** — idempotency, state machine enforcement, audit trail. This is the payments-domain signal.
2. **Operational maturity** — structured logs with correlation IDs flowing through the async boundary, RED + domain metrics, health endpoints.
3. **A written narrative** — every shortcut taken in code is paired with a "here's what changes in production" paragraph in the README.

Everything else (auth UI, multi-currency, real card handling) gets a stub or an assumption, written down.

## 2. Scope

### In scope
- Payment lifecycle: `Pending → Authorized → Captured → Settled`, with `Failed` and `Refunded` branches
- APIs: create, get, capture, refund, list/search (paged, filterable by status/date/merchant)
- Async workflow: **settlement processing** — captured payments are picked up by a background worker and settled after a simulated delay, with retry/backoff and a dead-letter state
- Idempotency keys on create/capture/refund
- Audit trail of every state transition (actor, timestamp, from → to, reason)
- React dashboard: payments list with filters, payment detail view with status timeline (audit trail rendered)
- Structured JSON logging, correlation ID propagation, Prometheus-style metrics endpoint, health/readiness endpoints
- Tests: domain state machine (unit), API integration tests for the idempotency and transition paths

### Out of scope (documented as assumptions/production notes)
- Real card data / acquirer integration — authorization is simulated (a fake gateway with configurable failure rate makes the Failed path demonstrable)
- AuthN/AuthZ — stub a merchant ID header; document OAuth2/mTLS approach
- Real broker — in-process worker; document the outbox → SQS/RabbitMQ migration path
- Multi-currency, partial captures/refunds — single full-amount operations only

## 3. Architecture

```
payment-platform/
├── docker-compose.yml            # postgres (+ optionally the api)
├── README.md                     # setup, architecture, tradeoffs, assumptions, production notes
├── backend/
│   ├── src/
│   │   ├── Payments.Api/         # controllers/endpoints, middleware, composition root
│   │   ├── Payments.Domain/      # Payment aggregate, state machine, events — zero dependencies
│   │   ├── Payments.Infrastructure/  # EF Core, repositories, outbox, fake gateway
│   │   └── Payments.Worker/      # settlement BackgroundService (hosted inside the Api process)
│   └── tests/
│       ├── Payments.Domain.Tests/
│       └── Payments.Api.Tests/   # WebApplicationFactory integration tests
└── frontend/                     # Vite + React + TS
```

Notes on the shape:

- **Domain project holds the state machine.** `Payment.Capture()`, `.Refund()`, etc. return a result or throw a domain error on illegal transitions; the transition table lives in one place and is trivially unit-testable. This is where most of the "code quality" evaluation happens — keep it small and obvious.
- **Worker hosted in the same process** as the API (a `BackgroundService`), but in its own project so the seam is visible. The narrative: "this deploys as one container today; splitting it into its own deployment is a project reference away."
- **Transactional outbox-lite:** rather than an in-memory `Channel<T>` (lost on crash — a bad look for payments), the capture handler writes a `settlement_jobs` row in the same transaction as the state change. The worker polls with `FOR UPDATE SKIP LOCKED`. This gives at-least-once semantics with zero extra infra and is the strongest correctness story available in a day. It also makes queue-depth metrics trivial (count of pending rows).

## 4. Data model

- `payments` — id (ULID/GUID), merchant_id, amount_minor, currency, status, card_last4 + brand only (PCI narrative: PAN/CVV never stored, never logged), created/updated timestamps, `xmin`/rowversion for optimistic concurrency
- `payment_transitions` — payment_id, from_status, to_status, actor (api-key id / "settlement-worker"), correlation_id, reason, timestamp. Append-only; this is the audit trail.
- `idempotency_keys` — key, merchant_id, operation, request_hash, response_snapshot, created_at. Same key + same hash → replay stored response; same key + different hash → `409/422`.
- `settlement_jobs` — payment_id, status (pending/processing/succeeded/dead), attempt_count, next_attempt_at, last_error, correlation_id

## 5. API surface

```
POST   /api/payments                 (Idempotency-Key header required)
GET    /api/payments/{id}
POST   /api/payments/{id}/capture    (Idempotency-Key required)
POST   /api/payments/{id}/refund     (Idempotency-Key required)
GET    /api/payments?status=&merchantId=&from=&to=&page=&pageSize=
GET    /api/payments/{id}/transitions   # audit trail, feeds the UI timeline
GET    /healthz  /readyz             # readiness checks DB connectivity
GET    /metrics                      # prometheus text format
```

Errors: RFC 7807 `application/problem+json` everywhere, with a stable `errorCode` field (`payment_not_capturable`, `idempotency_conflict`, ...). Illegal transitions are `409`, validation is `400`, unknown payment is `404`.

## 6. Observability plan

- **Logging:** Serilog, JSON console sink. Enrich every log with `CorrelationId`, `PaymentId`, `MerchantId`. Middleware reads/generates `X-Correlation-Id`; the ID is persisted on the settlement job row so the worker's logs carry the same ID across the async boundary — that's the end-to-end trace story the brief asks for. Redaction: card fields never enter log context by construction (domain model only ever holds last4).
- **Metrics:** `System.Diagnostics.Metrics` + `prometheus-net` (or OTel Prometheus exporter). RED via ASP.NET Core built-ins; domain counters `payments_created_total`, `payments_captured_total`, `payments_refunded_total`, `payments_failed_total` (tagged by status); worker gauges/counters: `settlement_queue_depth`, `settlement_lag_seconds`, `settlement_retries_total`, `settlement_dead_lettered_total`.
- **Tracing:** wire `ActivitySource` spans around API handlers and worker job processing, linked via the persisted correlation/trace context. No collector shipped — README explains the one-config-change path to an OTLP endpoint. This is exactly the "instrumentation seams, not a full stack" ask.
- **Health:** `/healthz` liveness (process up), `/readyz` checks Postgres. README notes how these map to k8s probes.

## 7. Resilience details worth getting right

- **Idempotency** as above — the replay-stored-response approach, not just a unique constraint, so retried creates return the original payment.
- **Optimistic concurrency** on payment rows; concurrent capture+refund can't both win.
- **Worker retry:** exponential backoff with jitter via `next_attempt_at`, max N attempts, then `dead` status + error log + counter increment. A dead job is visible in the DB and metrics — the operational answer to "what happens when settlement fails five times at 2am."
- **Fake gateway** has a configurable failure/latency profile so the Failed path and retries are actually exercisable in a demo, not just theoretical.

## 8. Frontend (kept deliberately lean)

Vite + React + TS, React Query for data fetching, minimal styling (plain CSS or Tailwind — no component-library sprawl). Two views:

1. **Payments list** — table, status filter chips, date range, search by ID, paged.
2. **Payment detail** — amounts, status badge, capture/refund action buttons (which demonstrate idempotency keys from the client side), and a vertical **status timeline rendered from the audit trail**. The timeline is the highest-signal-per-hour UI element in this exercise: it makes the audit table, state machine, and async settlement all visible at once.

Quality bar: typed API client, loading/error states handled, no `any`. Skip: auth screens, responsive polish, design-system ceremony.

## 9. Testing strategy (and the story for the README)

- **Domain unit tests** — exhaustive transition-table tests (legal + illegal), the highest-value tests in a payments system. Fast, no I/O.
- **API integration tests** — `WebApplicationFactory` + Testcontainers (or the compose Postgres): create→capture→settle happy path; idempotent replay returns identical response; conflicting idempotency key rejected; illegal transition → 409; refund after settle.
- **Worker test** — job picked up, retried on failure, dead-lettered after max attempts.
- Documented but skipped: contract tests, load tests, E2E browser tests — with a sentence each on when they'd earn their keep.

## 10. Build order (~1 day)

| # | Slice | ~Time |
|---|---|---|
| 1 | Repo scaffold, docker-compose (Postgres), solution + projects, CI-less but `dotnet test` green | 45m |
| 2 | Domain: Payment aggregate + state machine + unit tests | 1h |
| 3 | EF Core model, migrations, repositories; create + get endpoints | 1.5h |
| 4 | Idempotency middleware/handler + capture/refund endpoints + transitions audit | 1.5h |
| 5 | Settlement worker (jobs table, polling, backoff, dead-letter) | 1.5h |
| 6 | Observability pass: Serilog JSON + correlation ID, metrics, health endpoints, ActivitySource | 1h |
| 7 | Frontend: list + detail + timeline | 2h |
| 8 | Integration tests for the critical paths | 1h |
| 9 | README: setup, architecture (+ diagram), tradeoffs, assumptions, production considerations, future work | 1.5h |

Order matters: the README and observability are scheduled, not leftovers — they're half the rubric. If time runs short, cut frontend filters and the worker test before cutting any documentation.

## 11. Pre-written tradeoffs (seed for the README)

- **In-process worker + DB-as-queue** over a broker: zero reviewer setup, transactional enqueue, at-least-once. Give up: independent scaling, push semantics. Production: outbox stays, publisher relays to SQS/SNS or RabbitMQ; worker becomes its own deployment.
- **Polling with SKIP LOCKED** over LISTEN/NOTIFY: simpler, good enough at exercise scale; note the latency/throughput ceiling.
- **Modular monolith** over microservices: right-sized for the domain slice; boundaries are project references, so the split path is visible.
- **Simulated gateway**: keeps PCI scope at zero; document tokenization (network tokens / vault provider) as the production approach.
- **No auth**: merchant ID via header; document API-key or OAuth2 client-credentials per merchant, plus rate limiting.
