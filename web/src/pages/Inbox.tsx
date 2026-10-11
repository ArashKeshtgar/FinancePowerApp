import { useCallback, useEffect, useState } from 'react'
import { useSearchParams } from 'react-router'
import { api, dateTime, firstError, money, SOURCES, type DocumentRow } from '../api'
import { useBooks } from '../App'

const KINDS: Record<string, string> = { Invoice: 'Invoice (owed to us)', Receipt: 'Receipt', Adjustment: 'Adjustment', Bill: 'Bill (we owe)', Payment: 'Payment', Usage: 'Stock used', Reversal: 'Reversal' }

/** Financial documents from ClinicHub, LabFlow, ClinicReport and PharmaFlow, as they arrived, and what became of them. */
export default function Inbox() {
  const { entityId, can } = useBooks()
  const [params, setParams] = useSearchParams()
  const status = params.get('status') ?? ''
  const source = params.get('source') ?? ''
  const [rows, setRows] = useState<DocumentRow[] | null>(null)
  const [open, setOpen] = useState<number | null>(params.get('document') ? Number(params.get('document')) : null)
  const [flash, setFlash] = useState<string | null>(null)

  const load = useCallback(() => api.documents({ status, source, entityId }).then(setRows), [status, source, entityId])
  useEffect(() => { setRows(null); load() }, [load])
  const set = (k: string, v: string) => { const p = new URLSearchParams(params); if (v) p.set(k, v); else p.delete(k); p.delete('document'); setParams(p) }

  return (
    <>
      <div className="head">
        <div><h1>Documents from the applications</h1>
          <div className="muted">Claims, remittances, invoices, payments and purchases sent by the applications. No patient names: only amounts, payers and references.</div></div>
        {can('ledger.post') && <button onClick={async () => {
          try { const r = await api.repost(); setFlash(`${r.reposted} posted.`); load() } catch (e) { setFlash(firstError(e, 'Failed.')) }
        }}>Try waiting documents again</button>}
      </div>
      {flash && <p className="notice">{flash}</p>}
      <div className="split">
        <section className="card">
          <div className="filters">
            {['', 'NeedsMapping', 'Posted', 'Reversed'].map(s => (
              <button key={s} className={`chip ${status === s ? 'on' : ''}`} onClick={() => set('status', s)}>{s === '' ? 'All' : s === 'NeedsMapping' ? 'Waiting for a mapping' : s}</button>
            ))}
            <select value={source} onChange={e => set('source', e.target.value)} aria-label="Application" style={{ marginLeft: 'auto', width: 'auto' }}>
              <option value="">All applications</option>
              {['clinichub', 'labflow', 'clinicreport', 'pharmaflow'].map(s => <option key={s} value={s}>{SOURCES[s]}</option>)}
            </select>
          </div>
          <div className="table-wrap">
            <table className="data">
              <thead><tr><th>Date</th><th>From</th><th>Business</th><th>Kind</th><th>Party</th><th>Reference</th><th className="num">Total</th><th>Status</th></tr></thead>
              <tbody>
                {rows?.map(r => (
                  <tr key={r.id} className={`clickable ${open === r.id ? 'selected' : ''}`} onClick={() => setOpen(r.id)}>
                    <td className="nowrap">{r.date}</td><td>{SOURCES[r.source] ?? r.source}</td><td>{r.entity ?? <span className="muted">{r.sourceKey}?</span>}</td>
                    <td>{KINDS[r.kind] ?? r.kind}</td><td className="small">{r.partyType}{r.partyCode && r.partyCode !== r.partyType ? ` · ${r.partyCode}` : ''}</td>
                    <td className="mono small">{r.reference}</td><td className="num">{money(r.total)}</td>
                    <td><span className={`badge ${r.status}`} title={r.error}>{r.status === 'NeedsMapping' ? 'Waiting' : r.status}</span></td>
                  </tr>
                ))}
                {rows?.length === 0 && <tr><td colSpan={8} className="muted">Nothing here. Documents appear when an application sends them.</td></tr>}
              </tbody>
            </table>
          </div>
        </section>
        {open && <DocumentView key={open} id={open} onClose={() => setOpen(null)} />}
      </div>
    </>
  )
}

function DocumentView({ id, onClose }: { id: number; onClose: () => void }) {
  const [d, setD] = useState<(DocumentRow & { payloadJson: string }) | null>(null)
  useEffect(() => { api.document(id).then(setD) }, [id])
  if (!d) return <aside className="card pad side"><p className="muted">Loading…</p></aside>
  return (
    <aside className="card pad side">
      <div className="side-head"><h2>{KINDS[d.kind] ?? d.kind} {d.reference}</h2><button className="quiet" onClick={onClose} aria-label="Close">✕</button></div>
      <div className="muted small">{SOURCES[d.source] ?? d.source} · {d.externalId} · received {dateTime(d.receivedUtc)}</div>
      {d.error && <p className="notice">{d.error}<br /><span className="small">Map it under Setup → Posting maps (or Businesses for an unknown business); it is posted as soon as it is mapped.</span></p>}
      {d.journalEntryId && <a href={`/journal?entry=${d.journalEntryId}`}>Journal entry</a>}
      <pre className="payload">{JSON.stringify(JSON.parse(d.payloadJson), null, 2)}</pre>
    </aside>
  )
}
