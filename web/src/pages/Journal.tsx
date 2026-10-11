import { useCallback, useEffect, useState } from 'react'
import { useSearchParams } from 'react-router'
import { amount, api, dateTime, firstError, money, SOURCES, today, type Account, type EntryDetail, type EntryRow, type ManualLine, type Party } from '../api'
import { useBooks } from '../App'

/** Every entry, manual or from an application. Posted entries are never changed: they are reversed. */
export default function Journal() {
  const { entityId, can } = useBooks()
  const [params, setParams] = useSearchParams()
  const status = params.get('status') ?? ''
  const source = params.get('source') ?? ''
  const [rows, setRows] = useState<EntryRow[] | null>(null)
  const [open, setOpen] = useState<number | null>(params.get('entry') ? Number(params.get('entry')) : null)
  const [creating, setCreating] = useState(false)

  const load = useCallback(() => api.journal({ entityId, status, source }).then(setRows), [entityId, status, source])
  useEffect(() => { setRows(null); load() }, [load])
  const set = (k: string, v: string) => { const p = new URLSearchParams(params); if (v) p.set(k, v); else p.delete(k); setParams(p) }

  return (
    <>
      <div className="head">
        <div><h1>Journal</h1><div className="muted">Double entry. A posted entry is corrected by reversing it, never edited.</div></div>
        {can('ledger.post') && <button className="primary" onClick={() => { setOpen(null); setCreating(true) }}>New entry</button>}
      </div>
      <div className="split">
        <section className="card">
          <div className="filters">
            <select value={source} onChange={e => set('source', e.target.value)} aria-label="Source">
              <option value="">All sources</option>
              {Object.entries(SOURCES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </select>
            <select value={status} onChange={e => set('status', e.target.value)} aria-label="Status">
              <option value="">Any status</option><option>Draft</option><option>Posted</option><option>Reversed</option>
            </select>
          </div>
          <div className="table-wrap">
            <table className="data">
              <thead><tr><th>Date</th><th>Number</th><th>Business</th><th>Source</th><th>Memo</th><th className="num">Amount</th><th>Status</th></tr></thead>
              <tbody>
                {rows?.map(r => (
                  <tr key={r.id} className={`clickable ${open === r.id ? 'selected' : ''}`} onClick={() => { setCreating(false); setOpen(r.id) }}>
                    <td className="nowrap">{r.entryDate}</td><td className="mono small">{r.number}</td><td>{r.entity}</td>
                    <td>{SOURCES[r.source] ?? r.source}</td><td className="small">{r.memo}{r.latePosting && <span className="badge warn" title="Dated in a closed month; posted in the next open one"> late</span>}</td>
                    <td className="num">{money(r.total)}</td><td><span className={`badge ${r.status}`}>{r.status}</span></td>
                  </tr>
                ))}
                {rows?.length === 0 && <tr><td colSpan={7} className="muted">No entries.</td></tr>}
              </tbody>
            </table>
          </div>
        </section>
        {creating && <EntryForm onClose={() => setCreating(false)} onSaved={id => { setCreating(false); load(); setOpen(id) }} />}
        {!creating && open && <EntryView key={open} id={open} onChanged={load} onOpen={setOpen} onClose={() => setOpen(null)} />}
      </div>
    </>
  )
}

function EntryView({ id, onChanged, onOpen, onClose }: { id: number; onChanged: () => void; onOpen: (id: number) => void; onClose: () => void }) {
  const { can } = useBooks()
  const [e, setE] = useState<EntryDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const load = useCallback(() => api.entry(id).then(setE), [id])
  useEffect(() => { load() }, [load])
  const act = async (f: () => Promise<unknown>) => {
    setError(null)
    try { await f(); await load(); onChanged() } catch (x) { setError(firstError(x, 'Not done.')) }
  }
  if (!e) return <aside className="card pad side"><p className="muted">Loading…</p></aside>
  const r = e.entry
  return (
    <aside className="card pad side">
      <div className="side-head"><h2 className="mono">{r.number}</h2><button className="quiet" onClick={onClose} aria-label="Close">✕</button></div>
      <div className="muted small">{r.entity} · {r.entryDate} · month {r.period} · {SOURCES[r.source] ?? r.source} · <span className={`badge ${r.status}`}>{r.status}</span></div>
      {r.memo && <p style={{ margin: 0 }}>{r.memo}</p>}
      {r.latePosting && <p className="notice small">Dated in a closed month: posted in {r.period}.</p>}
      <table className="data">
        <thead><tr><th>Account</th><th className="num">Debit</th><th className="num">Credit</th></tr></thead>
        <tbody>
          {e.lines.map(l => (
            <tr key={l.lineNumber}>
              <td>{l.account}
                <div className="muted small">{[l.party, l.department, l.providerCode && `provider ${l.providerCode}`, l.taxCode, l.memo].filter(Boolean).join(' · ')}</div></td>
              <td className="num">{amount(l.debit)}</td><td className="num">{amount(l.credit)}</td>
            </tr>
          ))}
          <tr className="total"><td>Total</td><td className="num">{amount(e.lines.reduce((s, l) => s + l.debit, 0))}</td><td className="num">{amount(e.lines.reduce((s, l) => s + l.credit, 0))}</td></tr>
        </tbody>
      </table>
      <div className="muted small">Created by {e.createdBy}, {dateTime(e.createdUtc)}{e.postedUtc && <>; posted by {e.postedBy}, {dateTime(e.postedUtc)}</>}</div>
      {r.reversalOfId && <button className="link" onClick={() => onOpen(r.reversalOfId!)}>Reverses an earlier entry</button>}
      {e.reversedById && <button className="link" onClick={() => onOpen(e.reversedById!)}>Reversed by a later entry</button>}
      {r.documentId && <a href={`/inbox?document=${r.documentId}`}>The document it came from</a>}
      {error && <p className="error">{error}</p>}
      {can('ledger.post') && (
        <div className="actions">
          {r.status === 'Draft' && <>
            <button onClick={() => act(() => api.deleteEntry(id)).then(onClose)}>Delete draft</button>
            <button className="primary" onClick={() => act(() => api.postEntry(id))}>Post</button>
          </>}
          {r.status === 'Posted' && !r.reversalOfId && <button onClick={() => {
            if (window.confirm(`Reverse ${r.number}? A mirror entry is posted today (or in the next open month).`))
              act(async () => { const x = await api.reverseEntry(id); onOpen(x.id) })
          }}>Reverse</button>}
        </div>
      )}
    </aside>
  )
}

const blank = (): ManualLine => ({ accountId: 0, debit: 0, credit: 0, partyId: null, department: '', memo: '' })

function EntryForm({ onClose, onSaved }: { onClose: () => void; onSaved: (id: number) => void }) {
  const { entities, entityId } = useBooks()
  const [accounts, setAccounts] = useState<Account[]>([])
  const [parties, setParties] = useState<Party[]>([])
  const [form, setForm] = useState({ entityId: entityId ?? entities.find(e => e.isDefault)?.entityId ?? 0, date: today(), memo: '' })
  const [lines, setLines] = useState<ManualLine[]>([blank(), blank()])
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { api.accounts().then(a => setAccounts(a.filter(x => x.isActive && x.isPostable))); api.parties().then(setParties) }, [])

  const debits = lines.reduce((s, l) => s + (Number(l.debit) || 0), 0)
  const credits = lines.reduce((s, l) => s + (Number(l.credit) || 0), 0)
  const balanced = Math.round(debits * 100) === Math.round(credits * 100) && debits > 0
  const setLine = (i: number, patch: Partial<ManualLine>) => setLines(lines.map((l, j) => j === i ? { ...l, ...patch } : l))

  const save = async (post: boolean) => {
    setError(null)
    try {
      const r = await api.createEntry({
        ...form, post,
        lines: lines.filter(l => l.accountId && ((Number(l.debit) || 0) > 0 || (Number(l.credit) || 0) > 0))
          .map(l => ({ ...l, debit: Number(l.debit) || 0, credit: Number(l.credit) || 0, department: l.department || null, memo: l.memo || null })),
      })
      onSaved(r.id)
    } catch (x) { setError(firstError(x, 'Not saved.')) }
  }

  return (
    <aside className="card pad side wide">
      <div className="side-head"><h2>New journal entry</h2><button className="quiet" onClick={onClose} aria-label="Close">✕</button></div>
      <div className="form-row">
        <label>Business<select value={form.entityId} onChange={e => setForm({ ...form, entityId: Number(e.target.value) })}>
          {entities.filter(e => e.isActive).map(e => <option key={e.entityId} value={e.entityId}>{e.code} · {e.name}</option>)}</select></label>
        <label>Date<input type="date" value={form.date} onChange={e => setForm({ ...form, date: e.target.value })} /></label>
      </div>
      <label>Memo<input value={form.memo} onChange={e => setForm({ ...form, memo: e.target.value })} placeholder="e.g. October rent" /></label>
      <table className="data lines">
        <thead><tr><th>Account</th><th>Party</th><th className="num">Debit</th><th className="num">Credit</th><th></th></tr></thead>
        <tbody>
          {lines.map((l, i) => (
            <tr key={i}>
              <td><select value={l.accountId} onChange={e => setLine(i, { accountId: Number(e.target.value) })} aria-label="Account">
                <option value={0}>Choose…</option>{accounts.map(a => <option key={a.accountId} value={a.accountId}>{a.number} {a.name}</option>)}</select>
                <input className="small-input" value={l.memo ?? ''} onChange={e => setLine(i, { memo: e.target.value })} placeholder="Line memo" aria-label="Line memo" /></td>
              <td><select value={l.partyId ?? ''} onChange={e => setLine(i, { partyId: e.target.value ? Number(e.target.value) : null })} aria-label="Party">
                <option value="">—</option>{parties.map(p => <option key={p.partyId} value={p.partyId}>{p.name}</option>)}</select></td>
              <td><input className="num" inputMode="decimal" value={l.debit || ''} onChange={e => setLine(i, { debit: e.target.value as unknown as number, credit: 0 })} aria-label="Debit" /></td>
              <td><input className="num" inputMode="decimal" value={l.credit || ''} onChange={e => setLine(i, { credit: e.target.value as unknown as number, debit: 0 })} aria-label="Credit" /></td>
              <td>{lines.length > 2 && <button className="quiet" onClick={() => setLines(lines.filter((_, j) => j !== i))} aria-label="Remove line">✕</button>}</td>
            </tr>
          ))}
          <tr className="total"><td><button className="link" onClick={() => setLines([...lines, blank()])}>+ Add line</button></td><td>Total</td>
            <td className="num">{amount(debits)}</td><td className="num">{amount(credits)}</td><td /></tr>
        </tbody>
      </table>
      {!balanced && debits + credits > 0 && <p className="error small">Out of balance by {money(Math.abs(debits - credits))}.</p>}
      {error && <p className="error">{error}</p>}
      <div className="actions"><button onClick={() => save(false)}>Save as draft</button><button className="primary" disabled={!balanced} onClick={() => save(true)}>Post</button></div>
    </aside>
  )
}
