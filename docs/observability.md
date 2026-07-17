# Observability & logging


- **Logs:** Serilog, compact JSON (CLEF) on stdout — no file sinks, no rotation; the container
  runtime owns collection. View them with `docker compose logs -f api`, on the console of
  `dotnet run`, or in Grafana via the observability compose profile (see the README’s *Run it* section). Every property is its own
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
  `settlement_dead_lettered_total`, the queue gauges `settlement_queue_depth`,
  `settlement_lag_seconds`, `settlement_dead_jobs`, and `idempotency_replays_total{operation}` /
  `idempotency_conflicts_total{operation}` — a replay is a client retry served from storage, a
  conflict is the same key reused with a different payload, which is always a client bug.
  Label values are bounded to enums and route templates — never ids or gateway error strings,
  which is how a metrics backend gets taken down by its own instrumentation. Depth and lag are
  both exposed because depth alone can't distinguish a healthy backlog from a stuck one.
- **Traces:** an `ActivitySource` span per settlement job, tagged with the payment id and
  correlation id. ASP.NET Core already emits activities for requests; a background loop has
  none unless you make one. Under the observability profile these export to **Tempo** and
  render in Grafana (Explore → Tempo, or Drilldown → Traces): request waterfalls plus
  `settle-payment` spans carrying `payment.id`, `settlement.attempt` and
  `settlement.outcome`. Every request log line carries the trace id as `@tr`, and the Loki
  datasource lifts it into a **TraceID** link — log line to span waterfall in one click, and
  the Tempo datasource links back from spans to the surrounding logs. Without the profile
  (dev loop, tests) the exporter never registers and the spans stay no-ops.
