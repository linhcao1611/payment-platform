# Assumptions


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
