# Production considerations


**Authentication and authorization.** Replace the header with API keys or OAuth2
client-credentials per merchant, resolved in middleware into the same merchant scoping the
queries already apply. Add per-merchant rate limiting. The scoping rule is already enforced
at the repository boundary, so this changes *where identity comes from*, not every query.

**Authorization can be orphaned at the acquirer.** This is the sharpest known gap. `create`
calls the gateway *inside* the idempotency transaction. If that transaction rolls back after
the acquirer approved — a commit failure, a crash — the authorization exists on the customer's
card but no payment row does, and because a failure deliberately doesn't burn the key, the
client's retry authorizes a second time. The customer sees two holds. The fix is the same
shape as the settlement fix described in [tradeoffs.md](tradeoffs.md): pass a gateway idempotency key derived from the client's
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
as JSON on stdout; the `observability` compose profile (see the README’s *Run it* section) points a real
Grafana/Prometheus/Loki at
both without touching a line of application code, which is the property that matters. Traces
use `ActivitySource` with no exporter registered. Wiring real spans is a small, well-known
change — three OpenTelemetry packages and about a dozen lines in `Program.cs` registering the
`Payments.Worker` source alongside the ASP.NET one, plus a Tempo or Jaeger container to send
them to — not literally one line of config, and the docs shouldn't pretend otherwise. The
point stands, though: it's additive wiring at the composition root; no instrumented code
changes. The default stack ships seams, not a monitoring platform, because the seams are the
part that has to be right.

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
