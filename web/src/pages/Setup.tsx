import { useCallback, useEffect, useState } from 'react'
import { api, ApiError, firstError, money, type Account, type Entity, type Expense, type Maps, type Party, type Period } from '../api'
import { useBooks } from '../App'

type Tab = 'accounts' | 'maps' | 'entities' | 'parties' | 'periods' | 'expenses'
const TABS: [Tab, string][] = [['accounts', 'Chart of accounts'], ['maps', 'Posting maps'], ['entities', 'Businesses'], ['parties', 'Payers & suppliers'], ['periods', 'Months'], ['expenses', 'Power App expenses']]

export default function Setup() {
  const [tab, setTab] = useState<Tab>('accounts')
  return (
    <>
      <div className="head"><div><h1>Setup</h1><div className="muted">How the books are organised. Changes here apply to every business unless a map names one.</div></div></div>
      <div className="tabs" role="tablist">
        {TABS.map(([k, label]) => <button key={k} role="tab" aria-selected={tab === k} className={`tab ${tab === k ? 'on' : ''}`} onClick={() => setTab(k)}>{label}</button>)}
      </div>
      {tab === 'accounts' && <Accounts />}
      {tab === 'maps' && <PostingMaps />}
      {tab === 'entities' && <Entities />}
      {tab === 'parties' && <Parties />}
      {tab === 'periods' && <Periods />}
      {tab === 'expenses' && <Expenses />}
    </>
  )
}

function useMessage() {
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const run = async (f: () => Promise<unknown>, ok: string) => {
    setMsg(null)
    try { await f(); setMsg({ ok: true, text: ok }) } catch (e) { setMsg({ ok: false, text: firstError(e, 'Not saved.') }); throw e }
  }
  const view = msg && <p className={msg.ok ? 'notice ok' : 'error'}>{msg.text}</p>
  return { run, view }
}

const TYPES = ['Asset', 'Liability', 'Equity', 'Revenue', 'Expense']
const SUBTYPES = ['', 'Bank', 'Cash', 'Clearing', 'Receivable', 'Inventory', 'TaxRecoverable', 'Payable', 'TaxPayable', 'RetainedEarnings', 'CostOfSales']

function Accounts() {
  const { can } = useBooks()
  const [rows, setRows] = useState<Account[]>([])
  const [edit, setEdit] = useState<Partial<Account> | null>(null)
  const { run, view } = useMessage()
  const load = useCallback(() => api.accounts().then(setRows), [])
  useEffect(() => { load() }, [load])
  const save = () => run(async () => {
    const a = edit!
    await api.saveAccount(a.accountId ?? null, { number: a.number ?? '', name: a.name ?? '', type: a.type ?? 'Expense', subtype: a.subtype || undefined,
      isPostable: a.isPostable ?? true, isActive: a.isActive ?? true, gifiCode: a.gifiCode || undefined, description: a.description || undefined })
    setEdit(null); await load()
  }, 'Saved.').catch(() => {})

  return (
    <div className="split">
      <section className="card">
        <div className="filters">{can('admin') && <button className="primary small" onClick={() => setEdit({ type: 'Expense', isPostable: true, isActive: true })}>New account</button>}
          <span className="muted small">GIFI is the CRA line on the T2 return.</span></div>
        <div className="table-wrap"><table className="data">
          <thead><tr><th>Number</th><th>Name</th><th>Type</th><th>Subtype</th><th>GIFI</th></tr></thead>
          <tbody>{rows.map(a => (
            <tr key={a.accountId} className={`clickable ${!a.isActive ? 'inactive' : ''}`} onClick={() => can('admin') && setEdit(a)}>
              <td className="mono">{a.number}</td><td>{a.name}{a.description && <div className="muted small">{a.description}</div>}</td><td>{a.type}</td><td>{a.subtype}</td><td className="mono">{a.gifiCode}</td>
            </tr>))}</tbody>
        </table></div>
      </section>
      {edit && (
        <aside className="card pad side">
          <div className="side-head"><h2>{edit.accountId ? `Account ${edit.number}` : 'New account'}</h2><button className="quiet" onClick={() => setEdit(null)}>✕</button></div>
          <div className="form-row">
            <label>Number<input value={edit.number ?? ''} onChange={e => setEdit({ ...edit, number: e.target.value })} /></label>
            <label>GIFI<input value={edit.gifiCode ?? ''} maxLength={4} onChange={e => setEdit({ ...edit, gifiCode: e.target.value })} /></label>
          </div>
          <label>Name<input value={edit.name ?? ''} onChange={e => setEdit({ ...edit, name: e.target.value })} /></label>
          <div className="form-row">
            <label>Type<select value={edit.type} onChange={e => setEdit({ ...edit, type: e.target.value })}>{TYPES.map(t => <option key={t}>{t}</option>)}</select></label>
            <label>Subtype<select value={edit.subtype ?? ''} onChange={e => setEdit({ ...edit, subtype: e.target.value })}>{SUBTYPES.map(t => <option key={t} value={t}>{t || '—'}</option>)}</select></label>
          </div>
          <label>Description<input value={edit.description ?? ''} onChange={e => setEdit({ ...edit, description: e.target.value })} /></label>
          <label className="check"><input type="checkbox" checked={edit.isActive ?? true} onChange={e => setEdit({ ...edit, isActive: e.target.checked })} /> Active</label>
          {view}
          <div className="actions"><button className="primary" onClick={save}>Save</button></div>
        </aside>
      )}
    </div>
  )
}

function PostingMaps() {
  const { entities, can } = useBooks()
  const [maps, setMaps] = useState<Maps | null>(null)
  const [accounts, setAccounts] = useState<Account[]>([])
  const [form, setForm] = useState({ category: '', accountId: 0, entityId: '' })
  const { run, view } = useMessage()
  const load = useCallback(() => api.maps().then(setMaps), [])
  useEffect(() => { load(); api.accounts().then(a => setAccounts(a.filter(x => x.isActive && x.isPostable))) }, [load])
  const save = () => run(async () => {
    const r = await api.saveCategoryMap({ category: form.category, accountId: form.accountId, entityId: form.entityId ? Number(form.entityId) : null })
    setForm({ category: '', accountId: 0, entityId: '' }); await load()
    return r
  }, 'Saved. Waiting documents that use it have been posted.').catch(() => {})
  if (!maps) return <p className="muted">Loading…</p>
  const entityCode = (id?: number) => id ? entities.find(e => e.entityId === id)?.code : 'All'

  return (
    <div className="grid2">
      <section className="card">
        <div className="pad"><h2>Categories → accounts</h2>
          <p className="muted small">The applications name what a line is ("OHIP.Professional", "Pharmacy.Drug"). This says which account it goes to, for every business or for one.</p></div>
        <div className="table-wrap"><table className="data">
          <thead><tr><th>Category</th><th>Business</th><th>Account</th><th /></tr></thead>
          <tbody>{maps.categories.map(m => (
            <tr key={m.id} className="clickable" onClick={() => setForm({ category: m.category, accountId: m.accountId, entityId: m.entityId ? String(m.entityId) : '' })}>
              <td className="mono small">{m.category}{m.description && <div className="muted small" style={{ fontFamily: 'inherit' }}>{m.description}</div>}</td>
              <td>{entityCode(m.entityId)}</td><td>{m.account}</td>
              <td>{can('admin') && <button className="quiet" title="Remove" onClick={e => { e.stopPropagation(); if (window.confirm(`Remove the mapping for ${m.category}?`)) api.deleteCategoryMap(m.id).then(load) }}>✕</button>}</td>
            </tr>))}</tbody>
        </table></div>
      </section>
      <div style={{ display: 'grid', gap: '1rem', alignContent: 'start' }}>
        {can('admin') && (
          <section className="card pad">
            <h2>Map a category</h2>
            <label>Category<input value={form.category} onChange={e => setForm({ ...form, category: e.target.value })} placeholder="e.g. Uninsured.TravelMedicine" /></label>
            <label>Account<select value={form.accountId} onChange={e => setForm({ ...form, accountId: Number(e.target.value) })}>
              <option value={0}>Choose…</option>{accounts.map(a => <option key={a.accountId} value={a.accountId}>{a.number} {a.name}</option>)}</select></label>
            <label>For<select value={form.entityId} onChange={e => setForm({ ...form, entityId: e.target.value })}>
              <option value="">Every business</option>{entities.map(e => <option key={e.entityId} value={e.entityId}>{e.code} only</option>)}</select></label>
            {view}
            <div className="actions"><button className="primary" disabled={!form.category || !form.accountId} onClick={save}>Save</button></div>
          </section>
        )}
        <section className="card pad">
          <h2>Who owes → control accounts</h2>
          <table className="data"><tbody>{maps.controls.map(c => <tr key={c.controlMapId}><td>{c.partyType}</td><td>{c.side}</td><td>{c.account}</td></tr>)}</tbody></table>
          <h2 style={{ marginTop: '1rem' }}>How it was paid → accounts</h2>
          <table className="data"><tbody>{maps.cash.map(c => <tr key={c.method}><td>{c.method}</td><td>{c.account}</td></tr>)}</tbody></table>
        </section>
      </div>
    </div>
  )
}

function Entities() {
  const { entities, reloadEntities, can } = useBooks()
  const [edit, setEdit] = useState<Partial<Entity> | null>(null)
  const [source, setSource] = useState({ source: 'clinichub', sourceKey: '', entityId: 0 })
  const { run, view } = useMessage()
  const save = () => run(async () => {
    await api.saveEntity(edit!.entityId ?? null, { code: edit!.code ?? '', name: edit!.name ?? '', kind: edit!.kind ?? 'Clinic', hstNumber: edit!.hstNumber || null, isActive: edit!.isActive ?? true })
    setEdit(null); reloadEntities()
  }, 'Saved.').catch(() => {})
  const map = () => run(async () => { await api.mapSource(source.source, source.sourceKey, source.entityId); setSource({ ...source, sourceKey: '' }); reloadEntities() },
    'Mapped. Waiting documents from it have been posted.').catch(() => {})

  return (
    <div className="split">
      <section className="card">
        <div className="filters">{can('admin') && <button className="primary small" onClick={() => setEdit({ kind: 'Clinic', isActive: true })}>New business</button>}</div>
        <table className="data">
          <thead><tr><th>Code</th><th>Name</th><th>Kind</th><th>HST number</th><th>Claims ITCs</th><th>Documents from</th></tr></thead>
          <tbody>{entities.map(e => (
            <tr key={e.entityId} className={`clickable ${!e.isActive ? 'inactive' : ''}`} onClick={() => can('admin') && setEdit(e)}>
              <td className="mono">{e.code}{e.isDefault && <span className="badge"> default</span>}</td><td>{e.name}</td><td>{e.kind}</td><td className="mono">{e.hstNumber}</td>
              <td>{e.claimsInputTaxCredits ? 'Yes' : 'No (exempt supplies)'}</td><td className="small mono">{e.sources.join(', ')}</td>
            </tr>))}</tbody>
        </table>
      </section>
      <div style={{ display: 'grid', gap: '1rem', alignContent: 'start' }}>
        {edit && (
          <aside className="card pad">
            <div className="side-head"><h2>{edit.entityId ? edit.name : 'New business'}</h2><button className="quiet" onClick={() => setEdit(null)}>✕</button></div>
            <div className="form-row">
              <label>Code<input value={edit.code ?? ''} onChange={e => setEdit({ ...edit, code: e.target.value })} /></label>
              <label>Kind<select value={edit.kind} onChange={e => setEdit({ ...edit, kind: e.target.value })}>{['Clinic', 'Lab', 'Diagnostics', 'Pharmacy', 'Holding'].map(k => <option key={k}>{k}</option>)}</select></label>
            </div>
            <label>Name<input value={edit.name ?? ''} onChange={e => setEdit({ ...edit, name: e.target.value })} /></label>
            <label>HST number<input value={edit.hstNumber ?? ''} onChange={e => setEdit({ ...edit, hstNumber: e.target.value })} placeholder="123456789RT0001" /></label>
            <label className="check"><input type="checkbox" checked={edit.isActive ?? true} onChange={e => setEdit({ ...edit, isActive: e.target.checked })} /> Active</label>
            {view}
            <div className="actions"><button className="primary" onClick={save}>Save</button></div>
          </aside>
        )}
        {can('admin') && (
          <section className="card pad">
            <h2>Documents from an application go to…</h2>
            <p className="muted small">The application's name for the business: ClinicHub's clinic slug, LabFlow's "lab", PharmaFlow's "pharmacy".</p>
            <div className="form-row">
              <label>Application<select value={source.source} onChange={e => setSource({ ...source, source: e.target.value })}>{['clinichub', 'labflow', 'clinicreport', 'pharmaflow'].map(s => <option key={s}>{s}</option>)}</select></label>
              <label>Its name<input value={source.sourceKey} onChange={e => setSource({ ...source, sourceKey: e.target.value })} placeholder="harbourview" /></label>
            </div>
            <label>Business<select value={source.entityId} onChange={e => setSource({ ...source, entityId: Number(e.target.value) })}>
              <option value={0}>Choose…</option>{entities.map(e => <option key={e.entityId} value={e.entityId}>{e.code} · {e.name}</option>)}</select></label>
            {!edit && view}
            <div className="actions"><button className="primary" disabled={!source.sourceKey || !source.entityId} onClick={map}>Map</button></div>
          </section>
        )}
      </div>
    </div>
  )
}

const PARTY_TYPES = ['OHIP', 'ODB', 'WSIB', 'Province', 'Insurer', 'Patient', 'Provider', 'Supplier', 'Other']

function Parties() {
  const { can } = useBooks()
  const [rows, setRows] = useState<Party[]>([])
  const [edit, setEdit] = useState<Partial<Party> | null>(null)
  const { run, view } = useMessage()
  const load = useCallback(() => api.parties().then(setRows), [])
  useEffect(() => { load() }, [load])
  const save = () => run(async () => {
    const p = edit!
    await api.saveParty(p.partyId ?? null, { partyType: p.partyType ?? 'Supplier', code: p.code ?? '', name: p.name ?? '', email: p.email, phone: p.phone, paymentTermsDays: Number(p.paymentTermsDays ?? 30), isActive: p.isActive ?? true })
    setEdit(null); await load()
  }, 'Saved.').catch(() => {})
  return (
    <div className="split">
      <section className="card">
        <div className="filters">{can('admin') && <button className="primary small" onClick={() => setEdit({ partyType: 'Supplier', paymentTermsDays: 30, isActive: true })}>New</button>}
          <span className="muted small">Patients are one party: the applications keep who they are.</span></div>
        <table className="data">
          <thead><tr><th>Type</th><th>Code</th><th>Name</th><th>Terms</th></tr></thead>
          <tbody>{rows.map(p => <tr key={p.partyId} className={`clickable ${!p.isActive ? 'inactive' : ''}`} onClick={() => can('admin') && setEdit(p)}>
            <td>{p.partyType}</td><td className="mono small">{p.code}</td><td>{p.name}</td><td>{p.paymentTermsDays} days</td></tr>)}</tbody>
        </table>
      </section>
      {edit && (
        <aside className="card pad side">
          <div className="side-head"><h2>{edit.partyId ? edit.name : 'New payer or supplier'}</h2><button className="quiet" onClick={() => setEdit(null)}>✕</button></div>
          <div className="form-row">
            <label>Type<select value={edit.partyType} disabled={!!edit.partyId} onChange={e => setEdit({ ...edit, partyType: e.target.value })}>{PARTY_TYPES.map(t => <option key={t}>{t}</option>)}</select></label>
            <label>Code<input value={edit.code ?? ''} disabled={!!edit.partyId} onChange={e => setEdit({ ...edit, code: e.target.value })} /></label>
          </div>
          <label>Name<input value={edit.name ?? ''} onChange={e => setEdit({ ...edit, name: e.target.value })} /></label>
          <div className="form-row">
            <label>Email<input value={edit.email ?? ''} onChange={e => setEdit({ ...edit, email: e.target.value })} /></label>
            <label>Payment terms (days)<input type="number" value={edit.paymentTermsDays ?? 30} onChange={e => setEdit({ ...edit, paymentTermsDays: Number(e.target.value) })} /></label>
          </div>
          {view}
          <div className="actions"><button className="primary" onClick={save}>Save</button></div>
        </aside>
      )}
    </div>
  )
}

function Periods() {
  const { can } = useBooks()
  const [rows, setRows] = useState<Period[]>([])
  const { run, view } = useMessage()
  const load = useCallback(() => api.periods().then(setRows), [])
  useEffect(() => { load() }, [load])
  const next = rows.length ? Math.max(...rows.map(r => r.year)) + 1 : new Date().getFullYear()
  return (
    <section className="card">
      <div className="filters">
        <span className="muted small">The same months the Power App uses. A closed month takes no entries; documents dated in it post to the next open month.</span>
        {can('admin') && <button className="small" style={{ marginLeft: 'auto' }} onClick={() => run(() => api.addYear(next).then(load), `${next} added.`).catch(() => {})}>Add {next}</button>}
      </div>
      {view}
      <table className="data">
        <thead><tr><th>Month</th><th className="num">Entries</th><th>State</th><th /></tr></thead>
        <tbody>{rows.map(p => (
          <tr key={p.periodId}>
            <td className="mono">{p.label}</td><td className="num">{p.entries}</td>
            <td>{p.isClosed ? <span className="badge Reversed">Closed{p.closedDate ? ` ${p.closedDate.slice(0, 10)}` : ''}</span> : <span className="badge Posted">Open</span>}</td>
            <td className="actions-cell">{can('admin') && (p.isClosed
              ? <button className="small" onClick={() => window.confirm(`Reopen ${p.label}? Entries can then be dated in it again.`) && run(() => api.reopenPeriod(p.periodId).then(load), `${p.label} reopened.`).catch(() => {})}>Reopen</button>
              : <button className="small" onClick={() => run(() => api.closePeriod(p.periodId).then(load), `${p.label} closed.`).catch(() => {})}>Close</button>)}</td>
          </tr>))}</tbody>
      </table>
    </section>
  )
}

function Expenses() {
  const { can } = useBooks()
  const [rows, setRows] = useState<Expense[]>([])
  const [msg, setMsg] = useState<string | null>(null)
  const load = useCallback(() => api.expenses().then(setRows), [])
  useEffect(() => { load() }, [load])
  const post = async () => {
    try {
      const r = await api.postApprovedExpenses()
      setMsg(`${r.posted} posted.${r.skipped.length ? ` Skipped: ${r.skipped.join('; ')}` : ''}`)
      load()
    } catch (e) { setMsg(e instanceof ApiError ? e.message : 'Failed.') }
  }
  const waiting = rows.filter(r => r.status === 'Approved' && !r.journalEntryId).length
  return (
    <section className="card">
      <div className="filters">
        <span className="muted small">Expenses entered and approved in the Power App. Approved ones become bills of the head office: the amount paid to the category's expense account, owed to the vendor. The Power App's tax column is the income-tax deductible part; it doesn't record HST, so no input tax credit is taken.</span>
        {can('ledger.post') && <button className="primary small" style={{ marginLeft: 'auto' }} disabled={waiting === 0} onClick={post}>Post {waiting} approved</button>}
      </div>
      {msg && <p className="notice">{msg}</p>}
      <table className="data">
        <thead><tr><th>Date</th><th>Vendor</th><th>Category</th><th className="num">Amount</th><th className="num">Deductible</th><th>Status</th><th>Ledger</th></tr></thead>
        <tbody>{rows.map(x => (
          <tr key={x.expenseId}><td>{x.expenseDate}</td><td>{x.vendor}<div className="muted small">{x.description}</div></td><td>{x.category}</td>
            <td className="num">{money(x.amount)}</td><td className="num">{x.taxAmount ? money(x.taxAmount) : ''}</td><td>{x.status}</td>
            <td>{x.journalEntryId ? <a href={`/journal?entry=${x.journalEntryId}`}>Posted</a> : x.status === 'Approved' ? 'Waiting' : '—'}</td></tr>))}</tbody>
      </table>
    </section>
  )
}
