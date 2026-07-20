import { useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { capturePayment, getPayment, getTransitions, refundPayment } from '../api/client'
import type { PaymentResponse } from '../api/types'
import { formatAmount, formatCard, formatDateTime } from '../lib/format'
import { tempoTraceUrl } from '../lib/tempo'
import { retryTransportOnly } from '../lib/retry'
import { StatusBadge } from './StatusBadge'
import { Timeline } from './Timeline'
import { ErrorNotice } from './ErrorNotice'

const SETTLEMENT_POLL_MS = 2000

/**
 * Capture hands off to an async settlement worker, so `Captured` is not a resting
 * state — the payment becomes `Settled` on its own a second or two later. Poll only
 * in that window; every other status is resting and polling it would be pure noise.
 */
function pollInterval(payment: PaymentResponse | undefined): number | false {
  return payment?.status === 'Captured' ? SETTLEMENT_POLL_MS : false
}

export function PaymentDetail({ id, onBack }: { id: string; onBack: () => void }) {
  const queryClient = useQueryClient()
  const [showRefundForm, setShowRefundForm] = useState(false)
  const [refundReason, setRefundReason] = useState('')

  const paymentQuery = useQuery({
    queryKey: ['payment', id],
    queryFn: ({ signal }) => getPayment(id, signal),
    refetchInterval: ({ state }) => pollInterval(state.data),
    // Settlement lands 1-2s after capture. React Query pauses interval refetching
    // while the tab is hidden, which would strand the view on `Captured` if the user
    // glanced away at the wrong moment. The poll is short-lived and only runs in the
    // Captured window, so running it in the background costs nothing.
    refetchIntervalInBackground: true,
  })

  const payment = paymentQuery.data

  const transitionsQuery = useQuery({
    queryKey: ['transitions', id],
    queryFn: ({ signal }) => getTransitions(id, signal),
    // Kept in lockstep with the payment so the timeline visibly grows to `Settled`.
    refetchInterval: pollInterval(payment),
    refetchIntervalInBackground: true,
  })

  /**
   * The idempotency key identifies a logical action attempt, not an HTTP request.
   * It is minted once when the user clicks and held in a ref, so every retry of that
   * same attempt — React Query's own retry, or the user re-clicking after a network
   * error — replays the identical key and the server dedupes it. A fresh key per
   * request would make each retry a *new* capture and defeat the whole mechanism.
   * The ref is cleared on success: a later click is a genuinely new action, new key.
   * Capture and refund hold separate refs — they are different actions.
   */
  const captureKeyRef = useRef<string | null>(null)
  const refundKeyRef = useRef<string | null>(null)

  const refreshPayment = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['payment', id] }),
      queryClient.invalidateQueries({ queryKey: ['transitions', id] }),
      queryClient.invalidateQueries({ queryKey: ['payments'] }),
    ])
  }

  const captureMutation = useMutation({
    mutationFn: () => {
      captureKeyRef.current ??= crypto.randomUUID()
      return capturePayment(id, captureKeyRef.current)
    },
    retry: retryTransportOnly,
    onSuccess: async () => {
      captureKeyRef.current = null
      await refreshPayment()
    },
  })

  const refundMutation = useMutation({
    mutationFn: (reason: string | null) => {
      refundKeyRef.current ??= crypto.randomUUID()
      return refundPayment(id, reason, refundKeyRef.current)
    },
    retry: retryTransportOnly,
    onSuccess: async () => {
      refundKeyRef.current = null
      setShowRefundForm(false)
      setRefundReason('')
      await refreshPayment()
    },
  })

  const canCapture = payment?.status === 'Authorized'
  const canRefund = payment?.status === 'Captured' || payment?.status === 'Settled'
  const busy = captureMutation.isPending || refundMutation.isPending

  return (
    <section>
      <button type="button" className="back" onClick={onBack}>
        ← All payments
      </button>

      {paymentQuery.isPending ? <p className="muted">Loading payment…</p> : null}

      {paymentQuery.isError ? (
        <ErrorNotice error={paymentQuery.error} onRetry={() => void paymentQuery.refetch()} />
      ) : null}

      {payment ? (
        <>
          <header className="page-header detail-header">
            <div>
              <h1 className="amount">{formatAmount(payment.amountMinor, payment.currency)}</h1>
              <p className="muted">{payment.description ?? 'No description'}</p>
            </div>
            <StatusBadge status={payment.status} />
          </header>

          <div className="card">
            <dl className="details">
              <div>
                <dt>Payment id</dt>
                <dd>
                  <code>{payment.id}</code>
                </dd>
              </div>
              <div>
                <dt>Currency</dt>
                <dd>{payment.currency}</dd>
              </div>
              <div>
                <dt>Card</dt>
                <dd>{formatCard(payment.cardBrand, payment.cardLast4)}</dd>
              </div>
              <div>
                <dt>Created</dt>
                <dd>{formatDateTime(payment.createdAt)}</dd>
              </div>
              <div>
                <dt>Updated</dt>
                <dd>{formatDateTime(payment.updatedAt)}</dd>
              </div>
              <div>
                <dt>Trace</dt>
                <dd>
                  <a href={tempoTraceUrl(payment.id)} target="_blank" rel="noreferrer">
                    View in Tempo →
                  </a>
                </dd>
              </div>
            </dl>

            <div className="actions">
              <button
                type="button"
                className="btn btn-primary"
                disabled={!canCapture || busy}
                title={canCapture ? undefined : 'Capture is only legal from Authorized'}
                onClick={() => captureMutation.mutate()}
              >
                {captureMutation.isPending ? 'Capturing…' : 'Capture'}
              </button>
              <button
                type="button"
                className="btn"
                disabled={!canRefund || busy || showRefundForm}
                title={canRefund ? undefined : 'Refund is only legal from Captured or Settled'}
                onClick={() => setShowRefundForm(true)}
              >
                Refund
              </button>
              {payment.status === 'Captured' ? (
                <span className="pending-note" role="status">
                  <span className="pulse" aria-hidden="true" />
                  Awaiting settlement…
                </span>
              ) : null}
            </div>

            {showRefundForm ? (
              <form
                className="refund-form"
                onSubmit={(e) => {
                  e.preventDefault()
                  refundMutation.mutate(refundReason.trim() || null)
                }}
              >
                <label className="field">
                  <span>Reason (optional)</span>
                  <input
                    type="text"
                    value={refundReason}
                    autoFocus
                    placeholder="e.g. customer changed mind"
                    onChange={(e) => setRefundReason(e.target.value)}
                  />
                </label>
                <div className="refund-actions">
                  <button type="submit" className="btn btn-primary" disabled={busy}>
                    {refundMutation.isPending ? 'Refunding…' : 'Confirm refund'}
                  </button>
                  <button
                    type="button"
                    className="btn btn-quiet"
                    disabled={busy}
                    onClick={() => {
                      setShowRefundForm(false)
                      setRefundReason('')
                      // Abandoning the form ends this attempt; a later refund is a new action.
                      refundKeyRef.current = null
                    }}
                  >
                    Cancel
                  </button>
                </div>
              </form>
            ) : null}

            {captureMutation.isError ? <ErrorNotice error={captureMutation.error} /> : null}
            {refundMutation.isError ? <ErrorNotice error={refundMutation.error} /> : null}
          </div>

          <div className="card">
            <h2>Status timeline</h2>
            {transitionsQuery.isPending ? <p className="muted">Loading timeline…</p> : null}
            {transitionsQuery.isError ? (
              <ErrorNotice
                error={transitionsQuery.error}
                onRetry={() => void transitionsQuery.refetch()}
              />
            ) : null}
            {transitionsQuery.data ? <Timeline transitions={transitionsQuery.data} /> : null}
          </div>
        </>
      ) : null}
    </section>
  )
}
