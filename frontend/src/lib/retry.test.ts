import { describe, expect, it } from 'vitest'
import { ApiError } from '../api/client'
import { retryTransportOnly } from './retry'

// The retry policy decides whether a failed request is asked again. Getting it wrong in
// either direction is a real bug: retrying a 409 spams the server with requests that can
// never succeed and delays the explanation reaching the user; not retrying a network blip
// throws away the one case retries exist for.
describe('retryTransportOnly', () => {
  it('never retries a 4xx — the server understood and refused', () => {
    for (const status of [400, 401, 404, 409]) {
      const error = new ApiError(status, { status, detail: 'no' })
      expect(retryTransportOnly(0, error)).toBe(false)
    }
  })

  it('retries transport failures, twice at most', () => {
    const blip = new TypeError('fetch failed')
    expect(retryTransportOnly(0, blip)).toBe(true)
    expect(retryTransportOnly(1, blip)).toBe(true)
    expect(retryTransportOnly(2, blip)).toBe(false)
  })

  it('retries a 5xx — the server may recover', () => {
    expect(retryTransportOnly(0, new ApiError(500, { status: 500 }))).toBe(true)
  })
})
