import { useCallback, useEffect, useState } from 'react'

/**
 * Deliberately tiny router: two views don't justify a routing dependency.
 * pushState for navigation, popstate to stay in sync with the back button.
 */
export function useRoute(): { path: string; navigate: (to: string) => void } {
  const [path, setPath] = useState(() => window.location.pathname)

  useEffect(() => {
    const onPopState = () => setPath(window.location.pathname)
    window.addEventListener('popstate', onPopState)
    return () => window.removeEventListener('popstate', onPopState)
  }, [])

  const navigate = useCallback((to: string) => {
    window.history.pushState({}, '', to)
    setPath(to)
  }, [])

  return { path, navigate }
}

/** Returns the payment id for `/payments/{id}`, or null for the list route. */
export function matchPaymentDetail(path: string): string | null {
  const match = /^\/payments\/([^/]+)\/?$/.exec(path)
  return match ? decodeURIComponent(match[1]) : null
}
