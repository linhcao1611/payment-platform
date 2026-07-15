import { useEffect, useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { listPayments } from '../api/client'
import { PAYMENT_STATUSES } from '../api/types'
import type { PaymentStatus } from '../api/types'
import { dayBoundaryIso, formatAmount, formatCard, formatDateTime, shortId } from '../lib/format'
import { useDebounced } from '../lib/useDebounced'
import { StatusBadge } from './StatusBadge'
import { ErrorNotice } from './ErrorNotice'

const PAGE_SIZE = 10

export function PaymentsList({ onOpen }: { onOpen: (id: string) => void }) {
  const [status, setStatus] = useState<PaymentStatus | null>(null)
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)

  const debouncedSearch = useDebounced(search)

  // Any filter change invalidates the current page number — page 4 of the old
  // result set is meaningless in the new one.
  useEffect(() => {
    setPage(1)
  }, [status, from, to, debouncedSearch])

  const params = {
    status,
    from: from ? dayBoundaryIso(from, 'start') : undefined,
    to: to ? dayBoundaryIso(to, 'end') : undefined,
    search: debouncedSearch || undefined,
    page,
    pageSize: PAGE_SIZE,
  }

  const query = useQuery({
    queryKey: ['payments', params],
    queryFn: ({ signal }) => listPayments(params, signal),
    placeholderData: keepPreviousData, // avoids a full-table flash while paging
  })

  const data = query.data

  return (
    <section>
      <header className="page-header">
        <h1>Payments</h1>
        <p className="muted">Merchant acme</p>
      </header>

      <div className="filters">
        <div className="chips" role="group" aria-label="Filter by status">
          <button
            type="button"
            className={`chip${status === null ? ' chip-active' : ''}`}
            aria-pressed={status === null}
            onClick={() => setStatus(null)}
          >
            All
          </button>
          {PAYMENT_STATUSES.map((s) => (
            <button
              key={s}
              type="button"
              className={`chip${status === s ? ' chip-active' : ''}`}
              aria-pressed={status === s}
              onClick={() => setStatus(s)}
            >
              {s}
            </button>
          ))}
        </div>

        <div className="filter-row">
          <label className="field">
            <span>Search payment id</span>
            <input
              type="search"
              value={search}
              placeholder="Full or partial id"
              onChange={(e) => setSearch(e.target.value)}
            />
          </label>
          <label className="field">
            <span>From</span>
            <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
          </label>
          <label className="field">
            <span>To</span>
            <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
          </label>
        </div>
      </div>

      {query.isPending ? <p className="muted">Loading payments…</p> : null}

      {query.isError ? (
        <ErrorNotice error={query.error} onRetry={() => void query.refetch()} />
      ) : null}

      {data ? (
        data.items.length === 0 ? (
          <p className="empty">No payments match these filters.</p>
        ) : (
          <>
            <table className="table">
              <thead>
                <tr>
                  <th>Id</th>
                  <th className="num">Amount</th>
                  <th>Status</th>
                  <th>Card</th>
                  <th>Created</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((p) => (
                  <tr
                    key={p.id}
                    className="row"
                    tabIndex={0}
                    role="link"
                    onClick={() => onOpen(p.id)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault()
                        onOpen(p.id)
                      }
                    }}
                  >
                    <td>
                      <code>{shortId(p.id)}</code>
                    </td>
                    <td className="num">{formatAmount(p.amountMinor, p.currency)}</td>
                    <td>
                      <StatusBadge status={p.status} />
                    </td>
                    <td>{formatCard(p.cardBrand, p.cardLast4)}</td>
                    <td className="muted">{formatDateTime(p.createdAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>

            <div className="pager">
              <span className="muted">
                {data.totalCount} payment{data.totalCount === 1 ? '' : 's'}
              </span>
              <div className="pager-controls">
                <button
                  type="button"
                  disabled={data.page <= 1}
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                >
                  Previous
                </button>
                <span>
                  Page {data.page} of {Math.max(1, data.totalPages)}
                </span>
                <button
                  type="button"
                  disabled={data.page >= data.totalPages}
                  onClick={() => setPage((p) => p + 1)}
                >
                  Next
                </button>
              </div>
            </div>
          </>
        )
      ) : null}
    </section>
  )
}
