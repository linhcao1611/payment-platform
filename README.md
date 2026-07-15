# Payment Processing Platform

A simplified payment processing platform: backend API (.NET 10), React dashboard, and an
asynchronous settlement workflow. Built for the senior engineer technical exercise —
see [docs/PLAN.md](docs/PLAN.md) for the implementation plan.

## Setup

Prerequisites: .NET 10 SDK, Node 20+, Docker.

```bash
# 1. Start Postgres
docker compose up -d

# 2. Run the API (applies EF migrations on startup in Development)
cd backend
dotnet run --project src/Payments.Api

# 3. Run the dashboard
cd frontend
npm install
npm run dev
```

- API: http://localhost:5080 (Swagger at `/swagger`)
- Dashboard: http://localhost:5173
- Metrics: http://localhost:5080/metrics — Health: `/healthz`, `/readyz`
- Postgres: `localhost:5433` (host port 5433 to avoid clashing with a local Postgres on 5432)

```bash
# Run tests
cd backend && dotnet test
```

## Architecture overview

_TODO: components diagram — API, domain state machine, Postgres, settlement worker, dashboard._

| Project | Responsibility |
|---|---|
| `Payments.Domain` | Payment aggregate + state machine. No dependencies. |
| `Payments.Infrastructure` | EF Core persistence, idempotency store, settlement job queue (outbox-style), fake gateway. |
| `Payments.Worker` | Settlement `BackgroundService` — polls jobs, retry w/ backoff, dead-letter. |
| `Payments.Api` | HTTP endpoints, middleware (correlation ID, idempotency, problem+json errors), composition root. |

## Payment lifecycle

`Pending → Authorized → Captured → Settled`, with `Failed` and `Refunded` as terminal
branches. Every transition is recorded in an append-only audit table.

## Tradeoffs made

_TODO: in-process worker + DB-as-queue vs broker; polling SKIP LOCKED vs LISTEN/NOTIFY;
modular monolith vs services; simulated gateway; header-based merchant identity._

## Assumptions

_TODO: single currency, full-amount capture/refund only, simulated authorization,
no real card data (last4 + brand only)._

## Production considerations

_TODO: broker migration path, auth (API keys / OAuth2 client credentials), OTLP
exporter config, secrets management, horizontal scaling, PCI scope._

## Areas for future improvement

_TODO._
