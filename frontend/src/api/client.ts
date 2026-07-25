import type {
  DemoTrafficStatus,
  PagedResponse,
  PaymentListParams,
  PaymentResponse,
  ProblemDetails,
  TransitionResponse,
} from './types'

// Auth stub: the exercise scopes every request to a single merchant via header.
const MERCHANT_ID = 'acme'

// Empty by default, which keeps the relative `/api` path that both existing paths rely on:
// nginx proxies it under compose, Vite proxies it under `npm run dev`. The LocalStack deploy
// serves the dashboard from an S3 website, where there is no proxy in front of it, so
// Terraform builds with VITE_API_BASE set to the API Gateway stage URL and this same code
// reaches across origins instead.
const BASE_URL = `${import.meta.env.VITE_API_BASE ?? ''}/api`

/**
 * An API failure carrying the parsed problem+json body, so callers can show the
 * server's own `detail` (e.g. "cannot transition from Refunded to Captured")
 * rather than a generic message.
 */
export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemDetails

  constructor(status: number, problem: ProblemDetails) {
    super(problem.detail ?? problem.title ?? `Request failed with status ${status}`)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
  }

  get errorCode(): string | undefined {
    return this.problem.errorCode
  }
}

function isProblemDetails(value: unknown): value is ProblemDetails {
  return typeof value === 'object' && value !== null
}

async function toApiError(response: Response): Promise<ApiError> {
  // The body may not be problem+json (proxy errors, empty bodies); degrade gracefully.
  try {
    const body: unknown = await response.json()
    if (isProblemDetails(body)) {
      return new ApiError(response.status, body)
    }
  } catch {
    // fall through to the generic shape below
  }
  return new ApiError(response.status, {
    status: response.status,
    title: response.statusText || 'Request failed',
  })
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${BASE_URL}${path}`, {
    ...init,
    headers: {
      'X-Merchant-Id': MERCHANT_ID,
      ...init?.headers,
    },
  })

  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as T
}

export function listPayments(
  params: PaymentListParams,
  signal?: AbortSignal,
): Promise<PagedResponse<PaymentResponse>> {
  const query = new URLSearchParams()
  if (params.status) query.set('status', params.status)
  if (params.from) query.set('from', params.from)
  if (params.to) query.set('to', params.to)
  if (params.search) query.set('search', params.search)
  if (params.page) query.set('page', String(params.page))
  if (params.pageSize) query.set('pageSize', String(params.pageSize))

  const qs = query.toString()
  return request<PagedResponse<PaymentResponse>>(`/payments${qs ? `?${qs}` : ''}`, { signal })
}

export function getPayment(id: string, signal?: AbortSignal): Promise<PaymentResponse> {
  return request<PaymentResponse>(`/payments/${encodeURIComponent(id)}`, { signal })
}

export function getTransitions(id: string, signal?: AbortSignal): Promise<TransitionResponse[]> {
  return request<TransitionResponse[]>(`/payments/${encodeURIComponent(id)}/transitions`, { signal })
}

/**
 * `idempotencyKey` is supplied by the caller rather than generated here on purpose.
 * The key identifies a logical action attempt, not an HTTP request: if this call is
 * retried (React Query retry, or the user re-clicking after a network blip), the same
 * key must go out so the server dedupes it. Minting a fresh key per request — which is
 * what generating it inside this function would do — would defeat idempotency entirely.
 */
export function capturePayment(id: string, idempotencyKey: string): Promise<PaymentResponse> {
  return request<PaymentResponse>(`/payments/${encodeURIComponent(id)}/capture`, {
    method: 'POST',
    headers: { 'Idempotency-Key': idempotencyKey },
  })
}

/** See `capturePayment` on idempotency-key ownership. Refund carries its own distinct key. */
export function refundPayment(
  id: string,
  reason: string | null,
  idempotencyKey: string,
): Promise<PaymentResponse> {
  return request<PaymentResponse>(`/payments/${encodeURIComponent(id)}/refund`, {
    method: 'POST',
    headers: {
      'Idempotency-Key': idempotencyKey,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ reason }),
  })
}

/**
 * Not merchant data — this controls a demo aid, not a merchant's payments — but it's fine to
 * route through the same `request` helper; the API ignores the merchant header it carries along.
 */
export function getDemoTrafficStatus(signal?: AbortSignal): Promise<DemoTrafficStatus> {
  return request<DemoTrafficStatus>('/demo/traffic', { signal })
}

export function pauseDemoTraffic(): Promise<DemoTrafficStatus> {
  return request<DemoTrafficStatus>('/demo/traffic/pause', { method: 'POST' })
}

export function resumeDemoTraffic(): Promise<DemoTrafficStatus> {
  return request<DemoTrafficStatus>('/demo/traffic/resume', { method: 'POST' })
}
