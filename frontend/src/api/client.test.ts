import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

/**
 * `BASE_URL` is resolved once, at module load, from `import.meta.env`. So every test here has
 * to stub the environment *before* importing the client, which means a dynamic import after
 * `resetModules()` rather than a top-level one — a static import would bind the value from
 * whichever test ran first.
 */
async function loadClient(apiBase?: string) {
  vi.resetModules()
  if (apiBase === undefined) {
    vi.unstubAllEnvs()
  } else {
    vi.stubEnv('VITE_API_BASE', apiBase)
  }
  return import('./client')
}

function mockFetchOk() {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: true,
    json: async () => ({ items: [], page: 1, pageSize: 20, total: 0 }),
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

describe('api base url', () => {
  beforeEach(() => {
    vi.resetModules()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.unstubAllEnvs()
  })

  it('calls a relative /api path when VITE_API_BASE is unset', async () => {
    const fetchMock = mockFetchOk()
    const { listPayments } = await loadClient('')

    await listPayments({})

    expect(fetchMock.mock.calls[0][0]).toBe('/api/payments')
  })

  it('prefixes requests with VITE_API_BASE when it is set', async () => {
    const fetchMock = mockFetchOk()
    const { listPayments } = await loadClient('http://gateway.example/local')

    await listPayments({})

    expect(fetchMock.mock.calls[0][0]).toBe('http://gateway.example/local/api/payments')
  })

  it('prefixes every endpoint, not just the list', async () => {
    const fetchMock = mockFetchOk()
    const { getPayment } = await loadClient('http://gateway.example/local')

    await getPayment('pay_123')

    expect(fetchMock.mock.calls[0][0]).toBe('http://gateway.example/local/api/payments/pay_123')
  })

  it('still sends the merchant header', async () => {
    const fetchMock = mockFetchOk()
    const { listPayments } = await loadClient('')

    await listPayments({})

    expect(fetchMock.mock.calls[0][1].headers['X-Merchant-Id']).toBe('acme')
  })
})
