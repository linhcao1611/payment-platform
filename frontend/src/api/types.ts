export const PAYMENT_STATUSES = [
  'Pending',
  'Authorized',
  'Captured',
  'Settled',
  'Failed',
  'Refunded',
] as const

export type PaymentStatus = (typeof PAYMENT_STATUSES)[number]

export interface PaymentResponse {
  id: string
  merchantId: string
  amountMinor: number
  currency: string
  cardLast4: string
  cardBrand: string
  status: PaymentStatus
  description: string | null
  createdAt: string
  updatedAt: string
}

export interface TransitionResponse {
  fromStatus: PaymentStatus | null
  toStatus: PaymentStatus
  actor: string
  correlationId: string
  reason: string | null
  occurredAt: string
}

export interface PagedResponse<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
}

/** RFC7807 problem+json, plus the API's stable `errorCode` extension. */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  errorCode?: string
}

export interface PaymentListParams {
  status?: PaymentStatus | null
  from?: string
  to?: string
  search?: string
  page?: number
  pageSize?: number
}
