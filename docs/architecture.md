# Architectural overview


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
