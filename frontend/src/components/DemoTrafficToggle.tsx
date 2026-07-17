import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import {
  getDemoTrafficStatus,
  pauseDemoTraffic,
  resumeDemoTraffic,
} from '../api/client'
import type { DemoTrafficStatus } from '../api/types'

const QUERY_KEY = ['demo-traffic']

/**
 * Renders nothing outside the demo profile: `enabled` is false whenever DemoTraffic:Enabled
 * is false (the plain dev loop, tests), and showing a toggle that can't do anything would be
 * worse than showing nothing. Polls so the control stays correct if traffic is paused or
 * resumed from elsewhere — another tab, curl, the Postman collection.
 */
export function DemoTrafficToggle() {
  const queryClient = useQueryClient()

  const query = useQuery({
    queryKey: QUERY_KEY,
    queryFn: ({ signal }) => getDemoTrafficStatus(signal),
    refetchInterval: 5_000,
  })

  const status = query.data
  const [isMutating, setIsMutating] = useState(false)
  const [error, setError] = useState<string | null>(null)

  if (!status?.enabled) {
    return null
  }

  const toggle = async () => {
    setError(null)
    setIsMutating(true)
    try {
      const next = await (status.paused ? resumeDemoTraffic() : pauseDemoTraffic())
      queryClient.setQueryData<DemoTrafficStatus>(QUERY_KEY, next)
    } catch {
      setError('Could not reach the API — traffic state may be out of date.')
    } finally {
      setIsMutating(false)
    }
  }

  return (
    <div className="demo-traffic-bar">
      <span className={`demo-traffic-dot${status.paused ? ' demo-traffic-dot-paused' : ''}`} />
      <span className="muted">
        {status.paused
          ? 'Demo traffic: paused'
          : `Demo traffic: running (~${status.paymentsPerMinute}/min)`}
      </span>
      <button type="button" className="btn btn-quiet" onClick={() => void toggle()} disabled={isMutating}>
        {status.paused ? 'Resume' : 'Pause'}
      </button>
      {error ? <span className="demo-traffic-error">{error}</span> : null}
    </div>
  )
}
