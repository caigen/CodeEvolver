import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { Activity, ArrowRight, Check, CircleStop, Clock3, GitBranch, Play, Plus, RefreshCw, Server, XCircle } from 'lucide-react'
import './App.css'

type EvolutionStatus = 'draft' | 'running' | 'stopRequested' | 'stopped' | 'completed' | 'failed'
type EventStatus = 'pending' | 'processing' | 'completed' | 'failed' | 'cancelled'
type WorkItem = { id: string; title: string; description: string; status: string; result?: string }
type EvolutionEvent = { id: string; type: string; status: EventStatus; detail?: string; createdAt: string }
type Evolution = {
  id: string
  repositoryPath: string
  direction: string
  scope: string
  targetBranch: string
  status: EvolutionStatus
  summary?: string
  error?: string
  updatedAt: string
  workItems: WorkItem[]
  events: EvolutionEvent[]
}

const apiUrl = import.meta.env.VITE_API_URL ?? 'http://localhost:5278/api'
const emptyForm = { repositoryPath: '', direction: '', scope: '.', targetBranch: 'main' }

function App() {
  const [evolutions, setEvolutions] = useState<Evolution[]>([])
  const [selectedId, setSelectedId] = useState<string>()
  const [form, setForm] = useState(emptyForm)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const selected = evolutions.find((item) => item.id === selectedId) ?? evolutions[0]

  const refresh = async () => {
    try {
      const response = await fetch(`${apiUrl}/evolutions`)
      if (!response.ok) throw new Error(`API returned ${response.status}`)
      const data: Evolution[] = await response.json()
      setEvolutions(data)
      setSelectedId((current) => current ?? data[0]?.id)
      setError('')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not reach the API.')
    }
  }

  useEffect(() => {
    void refresh()
    const timer = window.setInterval(() => void refresh(), 1500)
    return () => window.clearInterval(timer)
  }, [])

  const createEvolution = async (event: FormEvent) => {
    event.preventDefault()
    setBusy(true)
    try {
      const response = await fetch(`${apiUrl}/evolutions`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(form),
      })
      if (!response.ok) throw new Error('Check all evolution fields and try again.')
      const evolution: Evolution = await response.json()
      setForm(emptyForm)
      setSelectedId(evolution.id)
      await refresh()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not create evolution.')
    } finally {
      setBusy(false)
    }
  }

  const sendCommand = async (command: 'start' | 'stop') => {
    if (!selected) return
    setBusy(true)
    try {
      const response = await fetch(`${apiUrl}/evolutions/${selected.id}/${command}`, { method: 'POST' })
      if (!response.ok) throw new Error(`Could not ${command} the evolution.`)
      await refresh()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : `Could not ${command} the evolution.`)
    } finally {
      setBusy(false)
    }
  }

  return (
    <main>
      <header>
        <div className="brand-mark"><Activity size={20} /></div>
        <div><h1>Code Evolver</h1><p>Autonomous repository operations</p></div>
        <div className={`connection ${error ? 'offline' : ''}`}><span />{error ? 'API offline' : 'System ready'}</div>
      </header>

      {error && <div className="error-banner"><XCircle size={18} />{error}<button onClick={() => void refresh()}><RefreshCw size={16} />Retry</button></div>}

      <div className="workspace">
        <aside>
          <form onSubmit={createEvolution}>
            <div className="section-title"><Plus size={17} /><h2>New evolution</h2></div>
            <label>Cloned repository path<input required value={form.repositoryPath} onChange={(e) => setForm({ ...form, repositoryPath: e.target.value })} placeholder="C:\work\repository" /></label>
            <label>Evolution direction<textarea required rows={4} value={form.direction} onChange={(e) => setForm({ ...form, direction: e.target.value })} placeholder="Improve API reliability and test coverage" /></label>
            <label>Scope<input required value={form.scope} onChange={(e) => setForm({ ...form, scope: e.target.value })} placeholder="src/api" /></label>
            <label>Target branch<div className="input-icon"><GitBranch size={16} /><input required value={form.targetBranch} onChange={(e) => setForm({ ...form, targetBranch: e.target.value })} /></div></label>
            <button className="primary" disabled={busy}><Plus size={17} />Create evolution</button>
          </form>

          <div className="evolution-list">
            <div className="section-title"><Server size={17} /><h2>Evolutions</h2><span>{evolutions.length}</span></div>
            {evolutions.length === 0 && <p className="empty">No evolutions yet.</p>}
            {evolutions.map((item) => (
              <button key={item.id} className={`evolution-row ${selected?.id === item.id ? 'selected' : ''}`} onClick={() => setSelectedId(item.id)}>
                <span className={`status-dot ${item.status}`} />
                <span><strong>{item.direction}</strong><small>{item.scope}</small></span>
                <ArrowRight size={15} />
              </button>
            ))}
          </div>
        </aside>

        <section className="detail">
          {!selected ? <div className="blank-state"><Activity size={30} /><h2>Create an evolution to begin</h2></div> : <>
            <div className="detail-head">
              <div><span className={`status-label ${selected.status}`}>{selected.status}</span><h2>{selected.direction}</h2><p>{selected.repositoryPath} <span>/</span> {selected.scope}</p></div>
              <div className="actions">
                <button className="icon-button" title="Refresh" onClick={() => void refresh()}><RefreshCw size={17} /></button>
                {selected.status !== 'running' && selected.status !== 'completed' && <button className="primary" disabled={busy} onClick={() => void sendCommand('start')}><Play size={16} />Start</button>}
                {selected.status === 'running' && <button className="danger" disabled={busy} onClick={() => void sendCommand('stop')}><CircleStop size={16} />Stop</button>}
              </div>
            </div>

            <div className="metrics">
              <div><span>Target</span><strong><GitBranch size={16} />{selected.targetBranch}</strong></div>
              <div><span>Work items</span><strong>{selected.workItems.length} / 5</strong></div>
              <div><span>Events</span><strong>{selected.events.length}</strong></div>
              <div><span>Updated</span><strong>{new Date(selected.updatedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</strong></div>
            </div>

            {selected.error && <div className="error-banner inline"><XCircle size={18} />{selected.error}</div>}
            <div className="content-grid">
              <section className="panel">
                <div className="section-title"><Check size={17} /><h3>Agent day plan</h3></div>
                {selected.workItems.length === 0 ? <p className="empty">Plan appears after the scan completes.</p> : selected.workItems.map((item, index) => <div className="work-item" key={item.id}><span>{index + 1}</span><div><strong>{item.title}</strong><p>{item.description}</p></div></div>)}
              </section>
              <section className="panel timeline-panel">
                <div className="section-title"><Clock3 size={17} /><h3>Event timeline</h3></div>
                <div className="timeline">
                  {[...selected.events].reverse().map((item) => <div className={`event ${item.status}`} key={item.id}><span className="event-icon">{item.status === 'completed' ? <Check size={13} /> : item.status === 'failed' ? <XCircle size={13} /> : <Clock3 size={13} />}</span><div><strong>{item.type.replaceAll('.', ' ')}</strong><small>{item.detail ?? item.status}</small></div></div>)}
                </div>
              </section>
            </div>
          </>}
        </section>
      </div>
    </main>
  )
}

export default App
