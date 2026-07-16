import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { Timeline } from './Timeline'
import type { TransitionResponse } from '../api/types'

// Vitest doesn't auto-clean the DOM between tests (no globals mode), and stale renders make
// getByText find duplicates from the previous test.
afterEach(cleanup)

function transition(overrides: Partial<TransitionResponse>): TransitionResponse {
  return {
    fromStatus: null,
    toStatus: 'Pending',
    actor: 'merchant:acme',
    correlationId: 'corr-1',
    reason: null,
    occurredAt: '2026-07-15T12:00:00Z',
    ...overrides,
  }
}

describe('Timeline', () => {
  it('shows an empty state rather than an empty list', () => {
    render(<Timeline transitions={[]} />)
    expect(screen.getByText(/no transitions recorded/i)).toBeTruthy()
  })

  it('tags a transition that shares the previous one\'s correlation id as "same request"', () => {
    // This is the timeline's whole point: the worker settles under the *capture's*
    // correlation id, and the tag is what makes that causal link visible in the UI.
    render(
      <Timeline
        transitions={[
          transition({ toStatus: 'Authorized', correlationId: 'create-corr' }),
          transition({ fromStatus: 'Authorized', toStatus: 'Captured', correlationId: 'capture-corr' }),
          transition({
            fromStatus: 'Captured',
            toStatus: 'Settled',
            actor: 'settlement-worker',
            correlationId: 'capture-corr',
          }),
        ]}
      />,
    )

    // Exactly one tag: the Settled row shares the Captured row's id; the Captured row
    // started a new request, so it must not be tagged.
    expect(screen.getAllByText(/same request/i)).toHaveLength(1)
  })

  it('marks system actors so a worker transition reads differently from a merchant one', () => {
    render(
      <Timeline
        transitions={[
          transition({}),
          transition({ fromStatus: 'Captured', toStatus: 'Settled', actor: 'settlement-worker' }),
        ]}
      />,
    )

    expect(screen.getByText('settlement-worker').className).toContain('actor-system')
    expect(screen.getByText('merchant:acme').className).not.toContain('actor-system')
  })

  it('renders the reason when one was recorded', () => {
    render(
      <Timeline
        transitions={[
          transition({ fromStatus: 'Captured', toStatus: 'Refunded', reason: 'customer request' }),
        ]}
      />,
    )
    expect(screen.getByText('customer request')).toBeTruthy()
  })
})
