/**
 * Minor units -> display string. Intl drives the fraction digits from the currency
 * itself, so zero-decimal currencies (JPY) aren't wrongly divided by 100.
 */
export function formatAmount(amountMinor: number, currency: string): string {
  const formatter = new Intl.NumberFormat(undefined, { style: 'currency', currency })
  const digits = formatter.resolvedOptions().maximumFractionDigits ?? 2
  return formatter.format(amountMinor / 10 ** digits)
}

export function formatDateTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return iso
  return date.toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
}

export function formatTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return iso
  return date.toLocaleString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })
}

/**
 * `<input type="date">` yields `yyyy-mm-dd`, but the API expects a full ISO-8601
 * instant with an offset (a bare date is rejected). We widen the date to a local-day
 * boundary — `from` to the start of the day, `to` to the *end* of it, so that picking
 * today as `to` still includes payments made today. Local, not UTC, because the times
 * shown in the table are local: the filter should mean the day the user sees.
 */
export function dayBoundaryIso(date: string, edge: 'start' | 'end'): string | undefined {
  const parts = date.split('-').map(Number)
  const [year, month, day] = parts
  if (parts.length !== 3 || parts.some(Number.isNaN) || !year || !month || !day) {
    return undefined
  }
  const instant =
    edge === 'start'
      ? new Date(year, month - 1, day, 0, 0, 0, 0)
      : new Date(year, month - 1, day, 23, 59, 59, 999)
  return Number.isNaN(instant.getTime()) ? undefined : instant.toISOString()
}

export function shortId(id: string): string {
  return id.slice(0, 8)
}

export function formatCard(brand: string, last4: string): string {
  const label = brand.charAt(0).toUpperCase() + brand.slice(1)
  return `${label} •••• ${last4}`
}
