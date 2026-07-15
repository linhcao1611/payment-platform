import { ApiError } from '../api/client'

/**
 * A 4xx means the server understood the request and refused it — a 404 or a 409
 * `invalid_state_transition` will never succeed on retry, and retrying only delays
 * the explanation reaching the user. Retry transport/5xx failures only.
 */
export function retryTransportOnly(failureCount: number, error: unknown): boolean {
  if (error instanceof ApiError && error.status >= 400 && error.status < 500) return false
  return failureCount < 2
}
