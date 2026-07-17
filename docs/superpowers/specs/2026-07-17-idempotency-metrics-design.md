# Idempotency replay/conflict metrics — design

## Problem

`PaymentsMetrics.cs` tracks payment lifecycle and settlement events, but nothing
observes the idempotency layer itself. Two facts about `IIdempotencyStore` are
currently invisible outside of logs/traces:

- **Replays** — a caller retried a request and got back a stored response
  instead of new work running. High replay volume signals retry storms
  (client-side timeouts, aggressive retry policies) even though nothing is
  actually failing.
- **Conflicts** — a caller reused an idempotency key with a *different*
  request payload, which `IdempotencyStore` rejects with
  `IdempotencyConflictException`. This is always a client bug (key reuse
  across logically different requests) and is worth seeing in real time
  rather than only in error logs.

## Metrics

Two new counters in `Payments.Infrastructure.Observability.PaymentsMetrics`,
labeled by `operation` (bounded to the existing literal values `create`,
`capture`, `refund` already passed into `IIdempotencyStore.ExecuteAsync` —
no new cardinality risk):

```csharp
public static readonly Counter IdempotencyReplays = Metrics.CreateCounter(
    "idempotency_replays_total", "Requests served from a stored idempotent response.",
    new CounterConfiguration { LabelNames = ["operation"] });

public static readonly Counter IdempotencyConflicts = Metrics.CreateCounter(
    "idempotency_conflicts_total", "Requests reusing an idempotency key with a different payload.",
    new CounterConfiguration { LabelNames = ["operation"] });
```

## Instrumentation point

Both counters increment inside the existing private `Replay` helper in
`IdempotencyStore.cs`. This is the single chokepoint both call sites already
funnel through:

1. The head-of-line hit in `ExecuteAsync` — `FindAsync` returns a stored key
   before any reservation is attempted.
2. The race-losing path — the reservation insert hits `23505`, the
   transaction rolls back, and the winner's row is looked up and replayed.

Instrumenting `Replay` itself means one change covers both paths with no
duplicated increment calls, and the operation label is read off the stored
entity (`stored.Operation`) rather than requiring a signature change:

```csharp
private static IdempotentResult Replay(IdempotencyKey stored, string requestHash, string key)
{
    if (stored.RequestHash != requestHash)
    {
        PaymentsMetrics.IdempotencyConflicts.WithLabels(stored.Operation).Inc();
        throw new IdempotencyConflictException(key);
    }

    PaymentsMetrics.IdempotencyReplays.WithLabels(stored.Operation).Inc();
    return new IdempotentResult(stored.ResponseStatusCode, stored.ResponseBody, Replayed: true);
}
```

The reservation-and-run path (a genuinely new request) is untouched — these
counters only fire on the replay/conflict branches, consistent with the
existing rule that counters increment after real, committed outcomes.

## Dashboard

One new timeseries panel in `ops/grafana/dashboards/payments.json`, in the
existing "Payment lifecycle" row, following the same `rate(...) * 60`
per-minute convention as the "Payment events / min" panel:

- `sum by (operation) (rate(idempotency_replays_total[1m])) * 60`
- `sum by (operation) (rate(idempotency_conflicts_total[1m])) * 60`

Conflicts get a red/orange color override, matching how `failed` and
`dead-lettered` are already highlighted elsewhere on the dashboard — a
conflict is a client bug surfacing live, not a routine event.

## Docs

`docs/observability.md`'s existing "Metrics:" bullet gets the two new
counters added to its list, in the same terse style as the current entry.

## Testing

`IdempotencyTests.cs` already exercises both branches:

- Same key + same payload → replay.
- Same key + different payload → `IdempotencyConflictException`.

Existing tests are extended to assert the relevant counter's value moved,
rather than adding new test files. `prometheus-net` counters expose `.Value`
in-process, so this needs no scraping or HTTP round trip.

## Out of scope

- No new metric for the "new work" branch — that's already covered by the
  existing domain counters (`payments_created_total`, etc.).
- No alerting rules for these counters (tracked separately, not part of this
  change).
- No gateway/settlement latency histogram (tracked separately, not part of
  this change).
