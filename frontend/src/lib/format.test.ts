import { describe, expect, it } from 'vitest'
import { dayBoundaryIso, formatAmount, formatCard, shortId } from './format'

describe('formatAmount', () => {
  it('divides minor units by the currency exponent', () => {
    expect(formatAmount(4200, 'USD')).toMatch(/42[.,]00/)
  })

  it('does not divide zero-decimal currencies', () => {
    // 500 JPY is ¥500, not ¥5.00 — the exponent comes from the currency, not a constant 100.
    const jpy = formatAmount(500, 'JPY')
    expect(jpy).toMatch(/500/)
    expect(jpy).not.toMatch(/5[.,]00/)
  })
})

describe('dayBoundaryIso', () => {
  // This function exists because <input type="date"> emits a bare yyyy-mm-dd, which the API
  // rejects; it widens to local day boundaries so "to = today" includes today's payments.
  it('widens to the local start and end of the day', () => {
    const start = dayBoundaryIso('2026-07-15', 'start')
    const end = dayBoundaryIso('2026-07-15', 'end')

    // Compare against Dates built the same local way the function builds them, so the
    // assertion holds in any timezone the test runs in.
    expect(start).toBe(new Date(2026, 6, 15, 0, 0, 0, 0).toISOString())
    expect(end).toBe(new Date(2026, 6, 15, 23, 59, 59, 999).toISOString())
  })

  it('start precedes end and both parse', () => {
    const start = dayBoundaryIso('2026-07-15', 'start')!
    const end = dayBoundaryIso('2026-07-15', 'end')!
    expect(new Date(start).getTime()).toBeLessThan(new Date(end).getTime())
  })

  it('returns undefined for input it cannot trust', () => {
    for (const bad of ['', 'banana', '2026-07', '2026-07-00', 'a-b-c']) {
      expect(dayBoundaryIso(bad, 'start')).toBeUndefined()
    }
  })
})

describe('display helpers', () => {
  it('shortId takes the first 8 characters', () => {
    expect(shortId('ad869535-d590-4ed1-ad48-9bbc0e31fdd6')).toBe('ad869535')
  })

  it('formatCard capitalises the brand and masks all but last4', () => {
    expect(formatCard('visa', '4242')).toBe('Visa •••• 4242')
  })
})
