import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import { api, money, SOURCES, type Dashboard as Data } from '../api'
import { Bars, useBooks } from '../App'

const PAYERS: Record<string, string> = { OHIP: 'OHIP', ODB: 'Ontario Drug Benefit', WSIB: 'WSIB', Province: 'Other provinces', Insurer: 'Insurers', Patient: 'Patients', Other: 'Other' }

export default function Dashboard() {
  const { entityId, entities } = useBooks()
  const [data, setData] = useState<Data | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { setData(null); api.dashboard(entityId).then(setData).catch(e => setError(e.message)) }, [entityId])
  const name = entityId ? entities.find(e => e.entityId === entityId)?.name : 'All businesses'

  if (error) return <p className="error">{error}</p>
  if (!data) return <p className="muted">Loading…</p>
  return (
    <>
      <div className="head">
        <div><h1>Dashboard</h1><div className="muted">{name} · this month and today's balances</div></div>
      </div>
      {(data.documentsWaiting > 0 || data.draftEntries > 0) && (
        <div className="notice">
          {data.documentsWaiting > 0 && <span><Link to="/inbox?status=NeedsMapping">{data.documentsWaiting} {data.documentsWaiting === 1 ? 'document waits' : 'documents wait'}</Link> for an account mapping. </span>}
          {data.draftEntries > 0 && <span><Link to="/journal?status=Draft">{data.draftEntries} draft {data.draftEntries === 1 ? 'entry' : 'entries'}</Link> not posted.</span>}
        </div>
      )}
      <div className="kpis">
        <Kpi label="Cash and bank" value={data.cash} />
        <Kpi label="Owed to us" value={data.receivables} />
        <Kpi label="We owe" value={data.payables} />
        <Kpi label="HST owing (net)" value={data.hstOwing} hint="HST collected less input tax credits" />
        <Kpi label="Revenue this month" value={data.revenueThisMonth} />
        <Kpi label="Expenses this month" value={data.expensesThisMonth} />
      </div>
      <div className="grid2">
        <section className="card pad"><h2>Revenue this month, by application</h2>
          <Bars rows={data.revenueBySource.map(r => ({ name: SOURCES[r.name] ?? r.name, amount: r.amount }))} format={money} /></section>
        <section className="card pad"><h2>Owed to us, by payer</h2>
          <Bars rows={data.receivablesByPayer.map(r => ({ name: PAYERS[r.name] ?? r.name, amount: r.amount }))} format={money} /></section>
        {!entityId && <section className="card pad"><h2>Revenue this month, by business</h2><Bars rows={data.revenueByEntity} format={money} /></section>}
        <section className="card pad"><h2>Net income, last six months</h2><Bars rows={data.monthlyNet} format={money} /></section>
      </div>
    </>
  )
}

function Kpi({ label, value, hint }: { label: string; value: number; hint?: string }) {
  return <div className="card kpi" title={hint}><span className="kpi-label">{label}</span><span className={`kpi-value ${value < 0 ? 'neg' : ''}`}>{money(value)}</span></div>
}
