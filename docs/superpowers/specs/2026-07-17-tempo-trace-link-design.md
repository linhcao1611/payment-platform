# Tempo trace link on the payment detail page — design

## Problem

The payment detail page has no way to jump into Tempo for a given payment.
Today the only path is manual: open Grafana, paste the payment id into the
dashboard's log search, expand a log line, and follow its trace-id link —
assuming a log line for that payment is still in the visible time range.

Investigation into what's actually traceable turned up a real gap: only the
settlement worker's `settle-payment` span (`SettlementWorker.cs:169-173`)
carries a `payment.id` tag. The API's HTTP request spans for create,
capture, and refund carry no payment-identifying tag at all — so a
`payment.id` search in Tempo would only ever surface the settlement leg of a
payment's life, never the create/capture/refund requests themselves. A
payment's lifecycle spans multiple separate traces (each HTTP request is its
own trace; the worker's processing is a distinct trace it starts itself) —
there's no single trace id per payment, only the correlation id ties them
together at the log level. A tag-based search is therefore the only thing
that can find "every trace touching this payment," and it only works if
every relevant span actually carries the tag.

## Backend: tag the request spans

`PaymentsController.cs` gets three `Activity.Current?.SetTag(...)` call
sites, mirroring the exact tag keys `SettlementWorker` already uses
(`payment.id`, `correlation.id`) so a single TraceQL query finds spans from
both the API and the worker:

- **`Create`**: inside the existing `if (created is not null)` block, right
  alongside the metrics increments. Tagging only fires for a genuine new
  create, not a replay — the same line the metrics already draw, since the
  payment id isn't cheaply available on the replay path (it would mean
  parsing the stored response body for no real benefit).
- **`Capture`**: right after `correlationId` is resolved, unconditionally.
  Unlike `Create`, the payment id here is the `id` route parameter — known
  up front regardless of whether the request turns out to be a replay — so
  there's no reason to skip tagging on replay.
- **`Refund`**: same shape as `Capture`.

`Activity.Current` is `null` whenever nothing is listening to the relevant
`ActivitySource` (true in the dev loop and in tests, per the existing OTel
gating in `Program.cs:82`), so `?.SetTag(...)` is a no-op there — identical
to how `SettlementWorker`'s own tagging already behaves.

## Backend tests

New `PaymentTracingTests.cs`. The shared test fixture never registers
OpenTelemetry (`OTEL_EXPORTER_OTLP_ENDPOINT` is unset in tests), so
`Activity.Current` is `null` there today and a test can't just check that
tagging code runs without erroring — it has to prove the tag actually lands
on an activity a real listener would see.

Each test registers its own `System.Diagnostics.ActivityListener`
(`ShouldListenTo = _ => true`, `Sample = (...) => ActivitySamplingResult.AllData`,
collecting stopped activities into a `ConcurrentBag<Activity>`) for the
duration of the test, independent of the app's own OTel DI registration —
subscribing *any* listener is what makes .NET's `Activity` infrastructure
start populating `Activity.Current` for the built-in ASP.NET Core hosting
source, the same mechanism `SettlementWorker`'s doc comment already
describes ("a null listener makes StartActivity a cheap no-op"). The test
then makes the request and asserts some stopped activity carries
`payment.id` equal to that test's own payment id, plus a `correlation.id`
tag. Filtering by the test's own freshly-minted GUID keeps this safe under
xUnit's default parallel test execution — no other activity in the process
will happen to carry the same id.

Three tests: create tags the new payment's id; capture tags the target
payment's id; refund tags the target payment's id.

## Frontend

- New `frontend/src/lib/tempo.ts`:
  - `GRAFANA_URL = 'http://localhost:3000'` — hardcoded, same treatment as
    the `MERCHANT_ID` auth stub already in `client.ts`. This app has no
    multi-environment config, and Grafana's port is fixed by
    `docker-compose.yml`.
  - `tempoTraceUrl(paymentId: string): string` builds a Grafana Explore URL:
    Tempo datasource `payments-tempo`, TraceQL query
    `` { .payment.id = "<paymentId>" } ``, range `now-7d`–`now` (generous
    enough to cover anything traced since the stack booted; seeded history
    predates tracing entirely and won't have spans regardless of range).
- `PaymentDetail.tsx`: one more row in the existing `dl.details` list —
  **Trace** → `View in Tempo →`, `target="_blank" rel="noreferrer"`.
- No visibility gating on whether Tempo/observability is actually running —
  same treatment as the README already gives the Grafana URL: documented
  and linked unconditionally, harmless to click if the observability
  profile isn't up.
- No new frontend automated tests — verified by running the dev server,
  clicking through, and confirming the resulting Tempo search actually
  returns the right spans, per the project's existing practice for UI
  changes.

## Out of scope

- No attempt to unify a payment's multiple traces into one (there is no
  single trace id per payment, and that's an architectural fact, not a gap
  this change can close).
- No tagging of the settlement worker's span beyond what it already has.
- No changes to `ops/tempo/tempo.yml` or the Tempo datasource config — plain
  TraceQL attribute search works on the existing default setup with no
  tuning required.
