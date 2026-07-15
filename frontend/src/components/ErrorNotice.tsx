import { ApiError } from '../api/client'

/**
 * Surfaces the server's own problem+json `detail` (and the stable errorCode) so a
 * failed capture explains *why* — e.g. "cannot transition from Refunded to Captured".
 */
export function ErrorNotice({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const isApi = error instanceof ApiError
  const title = isApi ? (error.problem.title ?? 'Request failed') : 'Something went wrong'
  const detail = isApi
    ? error.problem.detail
    : error instanceof Error
      ? error.message
      : String(error)

  return (
    <div className="notice notice-error" role="alert">
      <div className="notice-head">
        <strong>{title}</strong>
        {isApi && error.errorCode ? <code className="error-code">{error.errorCode}</code> : null}
      </div>
      {detail ? <p>{detail}</p> : null}
      {onRetry ? (
        <button type="button" className="btn btn-quiet" onClick={onRetry}>
          Retry
        </button>
      ) : null}
    </div>
  )
}
