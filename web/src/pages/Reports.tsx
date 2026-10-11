import { useEffect, useState } from 'react'
import { amount, api, money, today, yearStart, type AgingRow, type BalanceSheet, type HstLine, type IncomeStatement, type Section, type TrialBalance } from '../api'
import { useBooks } from '../App'

type Tab = 'income' | 'balance' | 'trial' | 'hst' | 'receivables' | 'payables'
const TABS: [Tab, string][] = [['income', 'Income statement'], ['balance', 'Balance sheet'], ['trial', 'Trial balance'], ['hst', 'HST return'], ['receivables', 'Receivables'], ['payables', 'Payables']]

export default function Reports() {
  const { entityId, entities } = useBooks()
  const [tab, setTab] = useState<Tab>('income')
  const [from, setFrom] = useState(yearStart())
  const [to, setTo] = useState(today())
  const name = entityId ? entities.find(e => e.entityId === entityId)?.name : 'All businesses combined'
  const ranged = tab === 'income' || tab === 'hst'

  return (
    <>
      <div className="head">
        <div><h1>Reports</h1><div className="muted">{name}. Posted entries only.</div></div>
        <div className="form-row">
          {ranged && <label>From<input type="date" value={from} onChange={e => setFrom(e.target.value)} /></label>}
          {tab !== 'receivables' && tab !== 'payables' && <label>{ranged ? 'To' : 'As of'}<input type="date" value={to} onChange={e => setTo(e.target.value)} /></label>}
        </div>
      </div>
      <div className="tabs" role="tablist">
        {TABS.map(([k, label]) => <button key={k} role="tab" aria-selected={tab === k} className={`tab ${tab === k ? 'on' : ''}`} onClick={() => setTab(k)}>{label}</button>)}
      </div>
      {tab === 'income' && <Income entityId={entityId} from={from} to={to} />}
      {tab === 'balance' && <Balance entityId={entityId} asOf={to} />}
      {tab === 'trial' && <Trial entityId={entityId} asOf={to} />}
      {tab === 'hst' && <Hst entityId={entityId} from={from} to={to} />}
      {tab === 'receivables' && <Aging entityId={entityId} payables={false} />}
      {tab === 'payables' && <Aging entityId={entityId} payables={true} />}
    </>
  )
}

function useLoad<T>(f: () => Promise<T>, deps: unknown[]) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { setData(null); f().then(setData).catch(e => setError(e.message)) }, deps) // eslint-disable-line react-hooks/exhaustive-deps
  return { data, error }
}

function SectionTable({ s }: { s: Section }) {
  return (
    <>
      <tr className="section"><td colSpan={2}>{s.name}</td></tr>
      {s.accounts.map(a => <tr key={`${a.accountId}-${a.name}`}><td><span className="mono muted">{a.number}</span> {a.name}</td><td className="num">{amount(a.balance)}</td></tr>)}
      <tr className="total"><td>Total {s.name.toLowerCase()}</td><td className="num">{money(s.total)}</td></tr>
    </>
  )
}

function Income({ entityId, from, to }: { entityId?: number; from: string; to: string }) {
  const { data, error } = useLoad<IncomeStatement>(() => api.incomeStatement(entityId, from, to), [entityId, from, to])
  if (error) return <p className="error">{error}</p>
  if (!data) return <p className="muted">Loading…</p>
  return (
    <section className="card pad statement">
      <div className="statement-head"><h2>Income statement, {data.from} to {data.to}</h2><a href={api.csv('income-statement', { entityId, from, to })}>CSV</a></div>
      <table className="data"><tbody>
        <SectionTable s={data.revenue} />
        <SectionTable s={data.expenses} />
        <tr className="grand"><td>Net income</td><td className={`num ${data.netIncome < 0 ? 'neg' : ''}`}>{money(data.netIncome)}</td></tr>
      </tbody></table>
    </section>
  )
}

function Balance({ entityId, asOf }: { entityId?: number; asOf: string }) {
  const { data, error } = useLoad<BalanceSheet>(() => api.balanceSheet(entityId, asOf), [entityId, asOf])
  if (error) return <p className="error">{error}</p>
  if (!data) return <p className="muted">Loading…</p>
  const balanced = Math.abs(data.assets.total - data.liabilitiesAndEquity) < 0.005
  return (
    <section className="card pad statement">
      <div className="statement-head"><h2>Balance sheet as of {data.asOf}</h2>{!balanced && <span className="badge NeedsMapping">Does not balance</span>}</div>
      <table className="data"><tbody>
        <SectionTable s={data.assets} />
        <SectionTable s={data.liabilities} />
        <SectionTable s={data.equity} />
        <tr><td>Earnings this year</td><td className="num">{money(data.currentEarnings)}</td></tr>
        <tr className="grand"><td>Liabilities and equity</td><td className="num">{money(data.liabilitiesAndEquity)}</td></tr>
      </tbody></table>
    </section>
  )
}

function Trial({ entityId, asOf }: { entityId?: number; asOf: string }) {
  const { data, error } = useLoad<TrialBalance>(() => api.trialBalance(entityId, asOf), [entityId, asOf])
  if (error) return <p className="error">{error}</p>
  if (!data) return <p className="muted">Loading…</p>
  return (
    <section className="card pad statement">
      <div className="statement-head"><h2>Trial balance as of {data.asOf}</h2><a href={api.csv('trial-balance', { entityId, asOf })}>CSV</a></div>
      <table className="data">
        <thead><tr><th>Account</th><th>Type</th><th className="num">Debit</th><th className="num">Credit</th></tr></thead>
        <tbody>
          {data.accounts.map(a => <tr key={a.accountId}><td><span className="mono muted">{a.number}</span> {a.name}</td><td>{a.type}</td><td className="num">{amount(a.debit)}</td><td className="num">{amount(a.credit)}</td></tr>)}
          <tr className="grand"><td colSpan={2}>Totals</td><td className="num">{money(data.totalDebit)}</td><td className="num">{money(data.totalCredit)}</td></tr>
        </tbody>
      </table>
    </section>
  )
}

function Hst({ entityId, from, to }: { entityId?: number; from: string; to: string }) {
  const { data, error } = useLoad<HstLine[]>(() => api.hst(entityId, from, to), [entityId, from, to])
  if (error) return <p className="error">{error}</p>
  if (!data) return <p className="muted">Loading…</p>
  const sum = (k: keyof HstLine) => data.reduce((s, r) => s + (r[k] as number), 0)
  return (
    <section className="card pad statement">
      <div className="statement-head"><h2>GST/HST return lines (GST34)</h2><a href={api.csv('hst', { entityId, from, to })}>CSV</a></div>
      <p className="muted small">Clinics and labs make exempt supplies: they charge no HST and claim no input tax credits. Prescription drugs are zero-rated: the pharmacy charges none and claims its credits back. Line 101 counts all revenue; check exempt and zero-rated amounts with your accountant before filing.</p>
      <table className="data">
        <thead><tr><th>Month</th><th>Business</th><th className="num">101 Sales</th><th className="num">103 HST collected</th><th className="num">106 Input tax credits</th><th className="num">109 Net tax</th></tr></thead>
        <tbody>
          {data.map(r => <tr key={`${r.period}-${r.entity}`}><td>{r.period}</td><td>{r.entity}</td><td className="num">{amount(r.sales)}</td><td className="num">{amount(r.taxCollected)}</td><td className="num">{amount(r.inputTaxCredits)}</td><td className="num">{amount(r.netTax)}</td></tr>)}
          {data.length === 0 && <tr><td colSpan={6} className="muted">Nothing posted in this range.</td></tr>}
          <tr className="grand"><td colSpan={2}>Total</td><td className="num">{money(sum('sales'))}</td><td className="num">{money(sum('taxCollected'))}</td><td className="num">{money(sum('inputTaxCredits'))}</td><td className="num">{money(sum('netTax'))}</td></tr>
        </tbody>
      </table>
    </section>
  )
}

const BUCKETS = ['Current', '1-30', '31-60', '61-90', '90+']

function Aging({ entityId, payables }: { entityId?: number; payables: boolean }) {
  const { data, error } = useLoad<AgingRow[]>(() => api.aging(entityId, payables), [entityId, payables])
  if (error) return <p className="error">{error}</p>
  if (!data) return <p className="muted">Loading…</p>
  const parties = [...new Set(data.map(r => r.party))]
  return (
    <section className="card pad statement">
      <div className="statement-head"><h2>{payables ? 'What we owe, by age' : 'What is owed to us, by age'}</h2><a href={api.csv('aging', { entityId, payables })}>CSV</a></div>
      <table className="data">
        <thead><tr><th>{payables ? 'Supplier / provider' : 'Payer'}</th>{BUCKETS.map(b => <th key={b} className="num">{b === 'Current' ? 'Not due' : `${b} days`}</th>)}<th className="num">Total</th></tr></thead>
        <tbody>
          {parties.map(p => {
            const rows = data.filter(r => r.party === p)
            return <tr key={p}><td>{p}</td>{BUCKETS.map(b => <td key={b} className="num">{amount(rows.filter(r => r.bucket === b).reduce((s, r) => s + r.balance, 0))}</td>)}
              <td className="num">{money(rows.reduce((s, r) => s + r.balance, 0))}</td></tr>
          })}
          {data.length === 0 && <tr><td colSpan={7} className="muted">Nothing outstanding.</td></tr>}
        </tbody>
      </table>
      <details><summary>{data.length} open {data.length === 1 ? 'item' : 'items'}</summary>
        <table className="data">
          <thead><tr><th>Business</th><th>Party</th><th>Reference</th><th>Date</th><th>Due</th><th className="num">Amount</th><th className="num">Balance</th></tr></thead>
          <tbody>{data.map(r => <tr key={r.itemId}><td>{r.entity}</td><td>{r.party}</td><td className="mono small">{r.reference}</td><td>{r.itemDate}</td><td>{r.dueDate}</td><td className="num">{amount(r.amount)}</td><td className="num">{amount(r.balance)}</td></tr>)}</tbody>
        </table>
      </details>
    </section>
  )
}
