# Areas for future improvement


In the order I'd actually do them:

1. **Gateway-side idempotency key on authorization** — closes the orphaned-authorization
   window described in [production-considerations.md](production-considerations.md), the same
   way settlement is already closed. The only known
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
