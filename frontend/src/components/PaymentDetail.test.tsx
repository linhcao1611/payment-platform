import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { PaymentDetail } from './PaymentDetail'
import type { PaymentResponse } from '../api/types'

/**
 * The idempotency-key lifecycle is the most consequential logic in this frontend: a key
 * identifies a logical action attempt, not an HTTP request. If a retry of the same attempt
 * minted a fresh key, every network blip would risk a double capture — the exact failure
 * idempotency exists to prevent. These tests drive the real component through a real
 * (mocked-fetch) retry and assert on the keys that actually went over the wire.
 */

const payment: PaymentResponse = {
  id: 'p1',
  merchantId: 'acme',
  amountMinor: 4200,
  currency: 'USD',
  cardLast4: '4242',
  cardBrand: 'visa',
  status: 'Authorized',
  description: 'test payment',
  createdAt: '2026-07-15T12:00:00Z',
  updatedAt: '2026-07-15T12:00:00Z',
}

function ok(body: unknown): Response {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    json: async () => body,
  } as Response
}

function renderDetail() {
  // Query retries off so mounting is instant; the *mutation* retry policy is part of the
  // component itself and is exactly what these tests exercise.
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <PaymentDetail id="p1" onBack={() => {}} />
    </QueryClientProvider>,
  )
}

async function clickCapture(): Promise<void> {
  const button = (await screen.findByRole('button', { name: /^captur/i })) as HTMLButtonElement
  await waitFor(() => expect(button.disabled).toBe(false))
  fireEvent.click(button)
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('capture idempotency keys', () => {
  it('reuses the same key across a retried attempt, and mints a new one for a new attempt', async () => {
    const captureKeys: string[] = []

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input)

        if (url.endsWith('/capture')) {
          const headers = init?.headers as Record<string, string>
          captureKeys.push(headers['Idempotency-Key'])

          // The first attempt dies on the wire — the client cannot know whether the server
          // acted. This is the exact situation the stable key exists for.
          if (captureKeys.length === 1) throw new TypeError('fetch failed')
          return ok(payment)
        }

        if (url.includes('/transitions')) return ok([])
        return ok(payment) // payment stays Authorized so Capture re-enables after success
      }),
    )

    renderDetail()

    // First click: network failure, then React Query retries the mutation.
    await clickCapture()
    await waitFor(() => expect(captureKeys).toHaveLength(2), { timeout: 4000 })

    // The retry is the same logical attempt, so the same key must have gone out — this is
    // what lets the server replay instead of capturing twice.
    expect(captureKeys[1]).toBe(captureKeys[0])
    expect(captureKeys[0]).toBeTruthy()

    // The attempt succeeded, so the ref was cleared. A second click is a genuinely new
    // action and must carry a genuinely new key.
    await clickCapture()
    await waitFor(() => expect(captureKeys).toHaveLength(3))
    expect(captureKeys[2]).not.toBe(captureKeys[0])
  })

  it('sends every capture with some idempotency key at all', async () => {
    const keys: (string | undefined)[] = []

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input)
        if (url.endsWith('/capture')) {
          const headers = (init?.headers ?? {}) as Record<string, string>
          keys.push(headers['Idempotency-Key'])
          return ok(payment)
        }
        if (url.includes('/transitions')) return ok([])
        return ok(payment)
      }),
    )

    renderDetail()
    await clickCapture()

    await waitFor(() => expect(keys).toHaveLength(1))
    expect(keys[0]).toMatch(/[0-9a-f-]{36}/)
  })
})
