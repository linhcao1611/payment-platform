import type { TransitionResponse } from '../api/types'
import { formatTime } from '../lib/format'
import { StatusBadge } from './StatusBadge'

/** Actors are `merchant:acme`-style; anything not a merchant acted on its own (e.g. the worker). */
function isSystemActor(actor: string): boolean {
  return !actor.startsWith('merchant:')
}

export function Timeline({ transitions }: { transitions: TransitionResponse[] }) {
  if (transitions.length === 0) {
    return <p className="muted">No transitions recorded yet.</p>
  }

  return (
    <ol className="timeline">
      {transitions.map((t, index) => {
        // Correlation ids group the transitions caused by one request. Marking where the
        // id changes makes it visible that settlement rides on the capture's correlation id.
        const previous = index > 0 ? transitions[index - 1] : null
        const newCorrelation = !previous || previous.correlationId !== t.correlationId

        return (
          <li
            key={`${t.occurredAt}-${t.toStatus}-${index}`}
            className={`timeline-item${isSystemActor(t.actor) ? ' timeline-item-system' : ''}`}
          >
            <div className="timeline-marker" aria-hidden="true" />
            <div className="timeline-body">
              <div className="timeline-transition">
                {t.fromStatus ? (
                  <StatusBadge status={t.fromStatus} />
                ) : (
                  <span className="badge badge-origin">start</span>
                )}
                <span className="timeline-arrow" aria-hidden="true">
                  →
                </span>
                <StatusBadge status={t.toStatus} />
              </div>

              <div className="timeline-meta">
                <span className={isSystemActor(t.actor) ? 'actor actor-system' : 'actor'}>
                  {t.actor}
                </span>
                <span className="timeline-dot" aria-hidden="true">
                  ·
                </span>
                <time dateTime={t.occurredAt}>{formatTime(t.occurredAt)}</time>
              </div>

              {t.reason ? <p className="timeline-reason">{t.reason}</p> : null}

              <div className="timeline-correlation" title="Correlation id">
                <code>{t.correlationId}</code>
                {!newCorrelation ? <span className="corr-tag">same request</span> : null}
              </div>
            </div>
          </li>
        )
      })}
    </ol>
  )
}
