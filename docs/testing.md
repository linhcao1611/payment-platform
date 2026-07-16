# Testing strategy


- **Domain unit tests** assert the full transition matrix, legal and illegal. Fast, no I/O,
  and the highest-value tests in a payments system.
- **Integration tests** run the real API against a throwaway Postgres (Testcontainers), so
  they don't depend on anyone having run `docker compose up` and never touch data someone is
  looking at. An in-memory provider would prove nothing here: `SKIP LOCKED`, unique-index
  contention and `xmin` concurrency are all Postgres behaviour, and they're the only things
  worth testing at that level. The two concurrency tests fire eight racing requests at one
  idempotency key and assert exactly one did the work.
- **Frontend tests** (Vitest + Testing Library, `npm test`) cover the money-adjacent logic
  rather than pixels. The one that matters drives the real detail component through a mocked
  network failure and asserts on the idempotency keys that actually went over the wire: a
  retry of the same attempt reuses the key — that's what lets the server replay instead of
  capturing twice — and a new attempt gets a fresh one. Around it: the never-retry-4xx
  policy, the date-boundary widening that once 500'd the API, and the timeline's
  "same request" tagging that makes the async settlement visibly caused by its capture.
- **CI** ([.github/workflows/ci.yml](../.github/workflows/ci.yml)) runs everything on every
  push: the full backend suite — Testcontainers works on GitHub's hosted runners because
  Docker is already there — and the frontend's lint, tests and type-checked build.
- **Deliberately skipped:** contract tests (one consumer, and it lives in this repo), load
  tests (they'd earn their keep once the connection-per-in-flight-payment tradeoff in [tradeoffs.md](tradeoffs.md)
  starts to bite), and browser E2E — the component tests above exercise the behaviour worth
  protecting without a browser to babysit.
