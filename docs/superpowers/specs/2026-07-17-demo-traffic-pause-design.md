# Pause/resume the demo traffic generator — design

## Problem

`DemoTrafficGenerator` (a `BackgroundService`) drives real HTTP traffic at the
API so the dashboards have genuine data to show, gated by
`DemoTraffic:Enabled`. That gate is startup-only: turning it off means
setting `DemoTraffic__Enabled=false` and restarting the `api` container. A
reviewer who wants to freeze the graphs or stop the payments list from
shifting mid-demo currently has no way to do that without a restart.

This adds a runtime pause/resume control, surfaced as a small toggle in the
dashboard UI.

## Backend

### `DemoTrafficControl`

New singleton, `Payments.Api/Demo/DemoTrafficControl.cs`:

```csharp
public sealed class DemoTrafficControl
{
    private volatile bool _paused;
    public bool IsPaused => _paused;
    public void Pause() => _paused = true;
    public void Resume() => _paused = false;
}
```

Registered once in `Program.cs` and injected into both the generator and the
new controller. No persistence — pause state is runtime-only and resets to
unpaused on restart, which is correct for a demo aid.

### `DemoTrafficGenerator` change

The existing loop in `ExecuteAsync` gets one new check before
`DriveOnePaymentAsync`:

```csharp
if (control.IsPaused)
{
    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
    continue;
}
```

`DemoTraffic:Enabled` still gates whether the loop runs at all (unchanged
behavior — the loop returns immediately if disabled, same as today).
`IsPaused` gates whether a *running* loop is currently doing anything. The
250ms poll means resume takes effect almost immediately without needing a
signal/event primitive.

### `DemoTrafficController`

New controller, unauthenticated (this isn't merchant data — no
`X-Merchant-Id` requirement), under `/api/demo/traffic`:

- `GET /api/demo/traffic` → `200` with
  `{ enabled, paused, paymentsPerMinute }`, always — this is what the
  frontend polls to decide whether to show the toggle at all.
- `POST /api/demo/traffic/pause` → `200` with the same shape, `paused: true`.
- `POST /api/demo/traffic/resume` → `200` with the same shape,
  `paused: false`.
- Both `POST` endpoints return `409` with `errorCode: demo_traffic_disabled`
  if `DemoTraffic:Enabled` is `false` — there's nothing running to pause, and
  saying so explicitly is more honest than silently accepting the call.

## Frontend

- `types.ts`: `DemoTrafficStatus { enabled: boolean; paused: boolean; paymentsPerMinute: number }`.
- `client.ts`: `getDemoTrafficStatus()`, `pauseDemoTraffic()`, `resumeDemoTraffic()` — same `request()` helper as the rest of the client.
- New `components/DemoTrafficToggle.tsx`:
  - `useQuery(['demo-traffic'], getDemoTrafficStatus, { refetchInterval: 5000 })` — polls so the control stays correct if traffic is paused/resumed from elsewhere (another tab, curl, the Postman collection).
  - `useMutation` for pause/resume; on success, writes the returned status straight into the query cache (`queryClient.setQueryData`) so the button flips without waiting for the next poll.
  - Renders `null` while the status query is loading, and `null` when `enabled` is `false` — the control is invisible outside the demo profile (e.g. the plain dev loop), rather than showing a toggle that does nothing.
  - When enabled: a small bar showing `Demo traffic: running (~{paymentsPerMinute}/min)` with a **Pause** button, or `Demo traffic: paused` with a **Resume** button.
- Mounted in `App.tsx`, inside `<main className="app">`, above the routed content — visible on both the list and detail views, since traffic keeps running regardless of which page is open.

## Testing

- Unit test for `DemoTrafficControl`: starts unpaused, `Pause()` sets
  `IsPaused` true, `Resume()` sets it back — no HTTP involved.
- Integration tests on the default (disabled) fixture host:
  - `GET /api/demo/traffic` → `enabled: false`.
  - `POST /pause` and `POST /resume` → `409`, `errorCode: demo_traffic_disabled`.
- One behavioral integration test on an isolated host
  (`PaymentsApiFixture.CreateIsolatedDatabaseAsync` + a new host variant with
  `DemoTraffic:Enabled=true` and a high `PaymentsPerMinute`), following the
  poll-for-convergence pattern already used in `SettlementTests`: wait for
  the payment count to grow, pause, confirm it stops growing over a short
  window, resume, confirm it grows again.
  - Requires extending `PaymentsApiFixture.CreateHost` to take an optional
    extra-config dictionary layered on top of the two keys it already sets
    (`workerEnabled` stays a required positional param; the new param is
    optional and defaults to none, so existing call sites are unchanged).
- No new frontend automated tests — verified by running the dev server and
  exercising the toggle in a browser, per the project's existing practice
  for UI changes.

## Out of scope

- No persistence of pause state across restarts.
- No auth/authorization on the demo-traffic endpoints — they control a demo
  aid, not merchant data, and the whole compose demo profile is already a
  local, anonymous-admin setup.
- No changes to the Postman collection (`ops/postman/payments.postman_collection.json`) — not requested.
