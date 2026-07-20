// Fixed by docker-compose.yml, same as the merchant-id auth stub in api/client.ts: this app has
// no multi-environment config, so a real base-url setting would be plumbing for a need that
// doesn't exist here.
const GRAFANA_URL = 'http://localhost:3000'

const TEMPO_DATASOURCE_UID = 'payments-tempo'

/**
 * Deep-links into Grafana Explore with a TraceQL search for every span tagged with this
 * payment's id — create, capture, refund, and (once it reaches the worker) settlement, since
 * PaymentsController and SettlementWorker tag the same `payment.id` key. A payment's lifecycle
 * has no single trace id (each request is its own trace), so a tag search across all of them is
 * the only thing that can find "every trace touching this payment."
 *
 * now-7d covers anything traced since the stack booted; seeded history predates tracing
 * entirely, so widening the range further wouldn't surface anything more for it.
 */
export function tempoTraceUrl(paymentId: string): string {
  const query = `{ .payment.id = "${paymentId}" }`
  const panes = {
    trace: {
      datasource: TEMPO_DATASOURCE_UID,
      queries: [
        {
          query,
          queryType: 'traceql',
          datasource: { type: 'tempo', uid: TEMPO_DATASOURCE_UID },
        },
      ],
      range: { from: 'now-7d', to: 'now' },
    },
  }

  const params = new URLSearchParams({
    schemaVersion: '1',
    panes: JSON.stringify(panes),
    orgId: '1',
  })

  return `${GRAFANA_URL}/explore?${params.toString()}`
}
