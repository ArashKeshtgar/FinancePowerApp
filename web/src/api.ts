// Typed client for FinancePowerApp.Api. Shapes mirror the C# records in src/FinancePowerApp.Api.

export class ApiError extends Error {
  constructor(public status: number, message: string, public fieldErrors: Record<string, string[]> = {}) { super(message) }
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const res = await fetch(url, {
    method, credentials: 'same-origin',
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : {},
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })
  if (res.status === 204) return undefined as T
  const text = await res.text()
  const data = text ? JSON.parse(text) : undefined
  if (!res.ok) throw new ApiError(res.status, data?.detail ?? data?.title ?? `Request failed (${res.status})`, data?.errors ?? {})
  return data as T
}

export const firstError = (e: unknown, fallback: string) =>
  e instanceof ApiError ? Object.values(e.fieldErrors).flat()[0] ?? e.message : fallback

export type Me = { name: string; permissions: string[] }
export type Entity = { entityId: number; code: string; name: string; kind: string; hstNumber?: string; isDefault: boolean; isActive: boolean; claimsInputTaxCredits: boolean; sources: string[] }
export type Account = { accountId: number; number: string; name: string; type: string; subtype?: string; isPostable: boolean; isActive: boolean; gifiCode?: string; description?: string }
export type Party = { partyId: number; partyType: string; code: string; name: string; email?: string; phone?: string; paymentTermsDays: number; isActive: boolean }
export type TaxCode = { code: string; name: string; ratePercent: number; recoverable: boolean; description?: string }
export type Period = { periodId: number; year: number; month: number; label: string; isClosed: boolean; closedDate?: string; entries: number }
export type EntryRow = { id: number; number: string; entity: string; entryDate: string; period: string; source: string; memo?: string; status: string; total: number; latePosting: boolean; documentId?: number; reversalOfId?: number }
export type Line = { lineNumber: number; accountId: number; account: string; debit: number; credit: number; partyId?: number; party?: string; department?: string; providerCode?: string; taxCode?: string; memo?: string }
export type EntryDetail = { entry: EntryRow; lines: Line[]; createdBy: string; createdUtc: string; postedBy?: string; postedUtc?: string; reversedById?: number }
export type ManualLine = { accountId: number; debit: number; credit: number; partyId?: number | null; department?: string | null; providerCode?: string | null; taxCode?: string | null; memo?: string | null }
export type DocumentRow = { id: number; source: string; externalId: string; sourceKey: string; entity?: string; kind: string; partyType?: string; partyCode?: string; date: string; reference: string; total: number; status: string; error?: string; journalEntryId?: number; receivedUtc: string; postedUtc?: string }
export type CategoryMap = { id: number; entityId?: number; category: string; accountId: number; account: string; description?: string }
export type Maps = { categories: CategoryMap[]; controls: { controlMapId: number; partyType: string; side: string; accountId: number; account: string }[]; cash: { method: string; accountId: number; account: string }[] }
export type Expense = { expenseId: number; amount: number; category?: string; expenseDate?: string; vendor?: string; description?: string; status?: string; taxAmount?: number; periodId?: number; journalEntryId?: number }

export type AccountBalance = { accountId: number; number: string; name: string; type: string; subtype?: string; debit: number; credit: number; balance: number }
export type Section = { name: string; accounts: AccountBalance[]; total: number }
export type TrialBalance = { asOf: string; accounts: AccountBalance[]; totalDebit: number; totalCredit: number }
export type IncomeStatement = { from: string; to: string; revenue: Section; expenses: Section; netIncome: number }
export type BalanceSheet = { asOf: string; assets: Section; liabilities: Section; equity: Section; currentEarnings: number; liabilitiesAndEquity: number }
export type HstLine = { period: string; entity: string; sales: number; taxCollected: number; inputTaxCredits: number; netTax: number }
export type AgingRow = { itemId: number; entity: string; partyType: string; party: string; source: string; reference: string; itemDate: string; dueDate: string; amount: number; paid: number; adjusted: number; balance: number; daysOverdue: number; bucket: string }
export type Named = { name: string; amount: number }
export type Dashboard = { cash: number; receivables: number; payables: number; hstOwing: number; revenueThisMonth: number; expensesThisMonth: number; revenueBySource: Named[]; revenueByEntity: Named[]; receivablesByPayer: Named[]; monthlyNet: Named[]; documentsWaiting: number; draftEntries: number }

const q = (params: Record<string, string | number | boolean | undefined | null>) => {
  const p = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== '') p.set(k, String(v))
  const s = p.toString()
  return s ? `?${s}` : ''
}

export const api = {
  me: () => request<Me>('GET', '/api/me'),
  entities: () => request<Entity[]>('GET', '/api/entities'),
  saveEntity: (id: number | null, e: { code: string; name: string; kind: string; hstNumber?: string | null; isActive: boolean }) =>
    request<Entity>(id ? 'PUT' : 'POST', id ? `/api/entities/${id}` : '/api/entities', e),
  mapSource: (source: string, sourceKey: string, entityId: number) => request<{ reposted: number }>('PUT', '/api/entity-sources', { source, sourceKey, entityId }),
  accounts: () => request<Account[]>('GET', '/api/accounts'),
  saveAccount: (id: number | null, a: Omit<Account, 'accountId'>) => request<Account>(id ? 'PUT' : 'POST', id ? `/api/accounts/${id}` : '/api/accounts', a),
  parties: (type?: string) => request<Party[]>('GET', `/api/parties${q({ type })}`),
  saveParty: (id: number | null, p: Omit<Party, 'partyId'>) => request<Party>(id ? 'PUT' : 'POST', id ? `/api/parties/${id}` : '/api/parties', p),
  taxCodes: () => request<TaxCode[]>('GET', '/api/tax-codes'),
  periods: () => request<Period[]>('GET', '/api/periods'),
  closePeriod: (id: number) => request<void>('POST', `/api/periods/${id}/close`),
  reopenPeriod: (id: number) => request<void>('POST', `/api/periods/${id}/reopen`),
  addYear: (year: number) => request<void>('POST', `/api/periods/year/${year}`),
  journal: (f: { entityId?: number; from?: string; to?: string; source?: string; status?: string; accountId?: number }) => request<EntryRow[]>('GET', `/api/journal${q(f)}`),
  entry: (id: number) => request<EntryDetail>('GET', `/api/journal/${id}`),
  createEntry: (e: { entityId: number; date: string; memo?: string; lines: ManualLine[]; post: boolean }) => request<{ id: number; number: string }>('POST', '/api/journal', e),
  postEntry: (id: number) => request<void>('POST', `/api/journal/${id}/post`),
  deleteEntry: (id: number) => request<void>('DELETE', `/api/journal/${id}`),
  reverseEntry: (id: number, date?: string, memo?: string) => request<{ id: number; number: string }>('POST', `/api/journal/${id}/reverse`, { date: date || null, memo: memo || null }),
  documents: (f: { status?: string; source?: string; entityId?: number }) => request<DocumentRow[]>('GET', `/api/documents${q(f)}`),
  document: (id: number) => request<DocumentRow & { payloadJson: string }>('GET', `/api/documents/${id}`),
  repost: () => request<{ reposted: number }>('POST', '/api/documents/repost'),
  maps: () => request<Maps>('GET', '/api/maps'),
  saveCategoryMap: (m: { entityId?: number | null; category: string; accountId: number; description?: string | null }) => request<{ reposted: number }>('PUT', '/api/maps/category', m),
  deleteCategoryMap: (id: number) => request<void>('DELETE', `/api/maps/category/${id}`),
  expenses: () => request<Expense[]>('GET', '/api/expenses'),
  postApprovedExpenses: () => request<{ posted: number; skipped: string[] }>('POST', '/api/expenses/post-approved'),
  dashboard: (entityId?: number) => request<Dashboard>('GET', `/api/reports/dashboard${q({ entityId })}`),
  trialBalance: (entityId?: number, asOf?: string) => request<TrialBalance>('GET', `/api/reports/trial-balance${q({ entityId, asOf })}`),
  incomeStatement: (entityId?: number, from?: string, to?: string) => request<IncomeStatement>('GET', `/api/reports/income-statement${q({ entityId, from, to })}`),
  balanceSheet: (entityId?: number, asOf?: string) => request<BalanceSheet>('GET', `/api/reports/balance-sheet${q({ entityId, asOf })}`),
  hst: (entityId?: number, from?: string, to?: string) => request<HstLine[]>('GET', `/api/reports/hst${q({ entityId, from, to })}`),
  aging: (entityId?: number, payables?: boolean) => request<AgingRow[]>('GET', `/api/reports/aging${q({ entityId, payables })}`),
  csv: (path: string, params: Record<string, string | number | boolean | undefined>) => `/api/reports/${path}${q({ ...params, format: 'csv' })}`,
}

export const money = (n: number) => n.toLocaleString('en-CA', { style: 'currency', currency: 'CAD' })
export const amount = (n: number) => n === 0 ? '' : n.toLocaleString('en-CA', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
export const today = () => new Date().toLocaleDateString('en-CA', { timeZone: 'America/Toronto' })
export const yearStart = () => `${today().slice(0, 4)}-01-01`
export const dateTime = (iso: string) => new Date(iso.endsWith('Z') ? iso : iso + 'Z').toLocaleString('en-CA', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'America/Toronto' })

export const SOURCES: Record<string, string> = {
  Manual: 'Manual', Expense: 'Power App expense', clinichub: 'ClinicHub', labflow: 'LabFlow', clinicreport: 'ClinicReport', pharmaflow: 'PharmaFlow',
}
