import { describe, expect, it } from 'vitest'
import { matchPaymentDetail } from './useRoute'

describe('matchPaymentDetail', () => {
  it('extracts the id from a detail path', () => {
    expect(matchPaymentDetail('/payments/abc-123')).toBe('abc-123')
    expect(matchPaymentDetail('/payments/abc-123/')).toBe('abc-123')
  })

  it('returns null for everything else', () => {
    for (const path of ['/', '/payments', '/payments/', '/payments/a/b', '/other']) {
      expect(matchPaymentDetail(path)).toBeNull()
    }
  })

  it('decodes an encoded id', () => {
    expect(matchPaymentDetail('/payments/a%20b')).toBe('a b')
  })
})
