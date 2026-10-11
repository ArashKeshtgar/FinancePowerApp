import { createContext, useContext, useEffect, useState } from 'react'
import { NavLink, Navigate, Route, Routes } from 'react-router'
import { api, type Entity, type Me } from './api'
import Dashboard from './pages/Dashboard'
import Inbox from './pages/Inbox'
import Journal from './pages/Journal'
import Reports from './pages/Reports'
import Setup from './pages/Setup'

type Books = { me: Me; entities: Entity[]; entityId?: number; setEntityId: (id?: number) => void; reloadEntities: () => void; can: (p: string) => boolean }
const BooksContext = createContext<Books | null>(null)
export const useBooks = () => useContext(BooksContext)!

const ENTITY_KEY = 'finance.entity'

export function App() {
  const [me, setMe] = useState<Me | null>(null)
  const [entities, setEntities] = useState<Entity[]>([])
  const [entityId, setEntity] = useState<number | undefined>(() => {
    try { const v = localStorage.getItem(ENTITY_KEY); return v ? Number(v) : undefined } catch { return undefined }
  })
  const [error, setError] = useState<string | null>(null)

  const reloadEntities = () => api.entities().then(setEntities)
  useEffect(() => {
    api.me().then(setMe).catch(e => setError(e.message))
    reloadEntities().catch(() => {})
  }, [])

  const setEntityId = (id?: number) => {
    setEntity(id)
    try { if (id) localStorage.setItem(ENTITY_KEY, String(id)); else localStorage.removeItem(ENTITY_KEY) } catch { /* private window */ }
  }

  if (error) return <p className="page error">Can't reach the finance API: {error}</p>
  if (!me) return <p className="page muted">Loading…</p>
  const can = (p: string) => me.permissions.includes(p) || me.permissions.includes('admin')

  return (
    <BooksContext.Provider value={{ me, entities, entityId, setEntityId, reloadEntities, can }}>
      <div className="shell">
        <aside className="sidebar">
          <div className="brand"><img src="/favicon.svg" alt="" width={28} height={28} /> FinancePowerApp</div>
          <label className="entity-pick">Books of
            <select value={entityId ?? ''} onChange={e => setEntityId(e.target.value ? Number(e.target.value) : undefined)}>
              <option value="">All businesses (combined)</option>
              {entities.filter(e => e.isActive).map(e => <option key={e.entityId} value={e.entityId}>{e.code} · {e.name}</option>)}
            </select>
          </label>
          <nav>
            <NavLink to="/dashboard">Dashboard</NavLink>
            <NavLink to="/journal">Journal</NavLink>
            <NavLink to="/inbox">Documents from apps</NavLink>
            <NavLink to="/reports">Reports</NavLink>
            <NavLink to="/setup">Setup</NavLink>
          </nav>
          <div className="who muted small">{me.name}</div>
        </aside>
        <main className="page">
          <Routes>
            <Route path="/" element={<Navigate replace to="/dashboard" />} />
            <Route path="/dashboard" element={<Dashboard />} />
            <Route path="/journal" element={<Journal />} />
            <Route path="/inbox" element={<Inbox />} />
            <Route path="/reports" element={<Reports />} />
            <Route path="/setup" element={<Setup />} />
            <Route path="*" element={<Navigate to="/dashboard" />} />
          </Routes>
        </main>
      </div>
    </BooksContext.Provider>
  )
}

export function Bars({ rows, format }: { rows: { name: string; amount: number }[]; format: (n: number) => string }) {
  const max = Math.max(1, ...rows.map(r => Math.abs(r.amount)))
  if (rows.length === 0) return <p className="muted small">Nothing yet.</p>
  return (
    <ul className="bars">
      {rows.map(r => (
        <li key={r.name}>
          <span className="bar-label">{r.name}</span>
          <span className="bar-track"><span className={`bar ${r.amount < 0 ? 'neg' : ''}`} style={{ width: `${(Math.abs(r.amount) / max) * 100}%` }} /></span>
          <span className="bar-value">{format(r.amount)}</span>
        </li>
      ))}
    </ul>
  )
}
