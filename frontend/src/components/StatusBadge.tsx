import type { PaymentStatus } from '../api/types'

// Positive: money is secured or moving as intended. Negative: money isn't coming.
// Progress: mid-flight, awaiting a further transition.
const TONE: Record<PaymentStatus, 'positive' | 'negative' | 'progress' | 'neutral'> = {
  Pending: 'neutral',
  Authorized: 'positive',
  Captured: 'progress',
  Settled: 'positive',
  Failed: 'negative',
  Refunded: 'negative',
}

export function StatusBadge({ status }: { status: PaymentStatus }) {
  return <span className={`badge badge-${TONE[status]}`}>{status}</span>
}
