import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { Activity, ArrowRight, Bot, Check, CircleStop, Clock3, FilePenLine, GitBranch, Play, Plus, RefreshCw, Save, Server, Timer, Trash2, X, XCircle } from 'lucide-react'
import './App.css'

type EvolutionStatus = 'draft' | 'running' | 'stopRequested' | 'stopped' | 'completed' | 'failed'
type EventStatus = 'pending' | 'processing' | 'completed' | 'failed' | 'cancelled'
type WorkItem = { id: string; title: string; description: string; status: string; result?: string }
type EvolutionEvent = { id: string; type: string; status: EventStatus; detail?: string; prompt?: string; logs: string[]; createdAt: string; startedAt?: string; completedAt?: string }
type AgentState = 'working' | 'done' | 'queued' | 'waiting' | 'stopped' | 'failed'
type AgentMember = { name: string; startEvent: string; completedEvent: string }
type Evolution = {
  id: string
  repositoryPath: string
  direction: string
  scope: string
  targetBranch: string
  status: EvolutionStatus
  summary?: string
  error?: string
  startedAt?: string
  completedAt?: string
  updatedAt: string
  workItems: WorkItem[]
  events: EvolutionEvent[]
}

const apiUrl = import.meta.env.VITE_API_URL ?? 'http://localhost:5278/api'
const emptyForm = { repositoryPath: '', direction: 'Improve e2e user experience & design and fix functionality issues.', scope: '.', targetBranch: 'main' }
const agentMembers: AgentMember[] = [
  { name: 'Scan agent', startEvent: 'scan.started', completedEvent: 'scan.completed' },
  { name: 'Plan agent', startEvent: 'plan.started', completedEvent: 'plan.completed' },
  { name: 'Worker agent', startEvent: 'work-item.started', completedEvent: 'work-item.completed' },
  { name: 'Reviewer agent', startEvent: 'review.started', completedEvent: 'review.completed' },
  { name: 'Gate agent', startEvent: 'gate.started', completedEvent: 'gate.completed' },
  { name: 'Merge agent', startEvent: 'change.merged', completedEvent: 'change.merged' },
]

const getAgentState = (events: EvolutionEvent[], member: AgentMember): { state: AgentState; eventType: string } => {
  const started = events.find((event) => event.type === member.startEvent)
  const completed = events.find((event) => event.type === member.completedEvent && event.status === 'completed')
  if (started?.status === 'processing') return { state: 'working', eventType: started.type }
  if (started?.status === 'failed') return { state: 'failed', eventType: started.type }
  if (started?.status === 'cancelled') return { state: 'stopped', eventType: started.type }
  if (completed || started?.status === 'completed') return { state: 'done', eventType: completed?.type ?? started?.type ?? member.completedEvent }
  if (started?.status === 'pending') return { state: 'queued', eventType: started.type }
  return { state: 'waiting', eventType: member.startEvent }
}

const readError = async (response: Response, fallback: string) => {
  const body = await response.json().catch(() => undefined)
  const validationMessage = body?.errors && Object.values(body.errors).flat().find((value) => typeof value === 'string')
  return validationMessage ?? body?.error ?? fallback
}

const formatDuration = (start?: string, end?: string, now = Date.now()) => {
  if (!start) return 'Not started'
  const seconds = Math.max(0, Math.floor(((end ? new Date(end).getTime() : now) - new Date(start).getTime()) / 1000))
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  return `${hours > 0 ? `${hours}:` : ''}${String(minutes).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`
}

const formatHeartbeat = (start?: string, now = Date.now()) => {
  if (!start) return '00:00'
  const seconds = Math.max(0, Math.floor((now - new Date(start).getTime()) / 5000) * 5)
  return `${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`
}

const formatTimestamp = (value?: string) => value ? new Date(value).toLocaleString() : 'Not recorded'

function App() {
  const [evolutions, setEvolutions] = useState<Evolution[]>([])
  const [selectedId, setSelectedId] = useState<string>()
  const [form, setForm] = useState(emptyForm)
  const [editingId, setEditingId] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [now, setNow] = useState(0)
  const selected = evolutions.find((item) => item.id === selectedId) ?? evolutions[0]
  const isActive = selected?.status === 'running' || selected?.status === 'stopRequested'
  const activeEvent = selected?.events.find((item) => item.status === 'processing')

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
    void fetch(`${apiUrl}/repository`)
      .then(async (response) => {
        if (!response.ok) return
        const data: { repositoryPath: string } = await response.json()
        setForm((current) => current.repositoryPath ? current : { ...current, repositoryPath: data.repositoryPath })
      })
    const timer = window.setInterval(() => void refresh(), 1500)
    const clock = window.setInterval(() => setNow(Date.now()), 1000)
    return () => { window.clearInterval(timer); window.clearInterval(clock) }
  }, [])

  const createEvolution = async (event: FormEvent) => {
    event.preventDefault()
    setBusy(true)
    try {
      const response = await fetch(editingId ? `${apiUrl}/evolutions/${editingId}` : `${apiUrl}/evolutions`, {
        method: editingId ? 'PUT' : 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(form),
      })
      if (!response.ok) throw new Error(await readError(response, 'Check all evolution fields and try again.'))
      const evolution: Evolution = await response.json()
      setForm((current) => ({ ...emptyForm, repositoryPath: current.repositoryPath }))
      setEditingId(undefined)
      setSelectedId(evolution.id)
      await refresh()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not create evolution.')
    } finally {
      setBusy(false)
    }
  }

  const beginEdit = () => {
    if (!selected || isActive) return
    setEditingId(selected.id)
    setForm({ repositoryPath: selected.repositoryPath, direction: selected.direction, scope: selected.scope, targetBranch: selected.targetBranch })
  }

  const cancelEdit = () => {
    setEditingId(undefined)
    setForm((current) => ({ ...emptyForm, repositoryPath: current.repositoryPath }))
  }

  const deleteEvolution = async () => {
    if (!selected || isActive || !window.confirm(`Delete evolution “${selected.direction}”?`)) return
    setBusy(true)
    try {
      const response = await fetch(`${apiUrl}/evolutions/${selected.id}`, { method: 'DELETE' })
      if (!response.ok) throw new Error(await readError(response, 'Could not delete the evolution.'))
      if (editingId === selected.id) cancelEdit()
      setSelectedId(undefined)
      await refresh()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not delete the evolution.')
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
            <div className="section-title">{editingId ? <FilePenLine size={17} /> : <Plus size={17} />}<h2>{editingId ? 'Edit evolution' : 'New evolution'}</h2></div>
            <label>Cloned repository path<input required value={form.repositoryPath} onChange={(e) => setForm({ ...form, repositoryPath: e.target.value })} placeholder="C:\work\repository" /></label>
            <label>Evolution direction<textarea required rows={4} value={form.direction} onChange={(e) => setForm({ ...form, direction: e.target.value })} placeholder="Improve API reliability and test coverage" /></label>
            <label>Scope<input required value={form.scope} onChange={(e) => setForm({ ...form, scope: e.target.value })} placeholder="src/api" /></label>
            <label>Target branch<div className="input-icon"><GitBranch size={16} /><input required value={form.targetBranch} onChange={(e) => setForm({ ...form, targetBranch: e.target.value })} /></div></label>
            <div className="form-actions">
              {editingId && <button type="button" className="secondary" disabled={busy} onClick={cancelEdit}><X size={17} />Cancel</button>}
              <button className="primary" disabled={busy}>{editingId ? <Save size={17} /> : <Plus size={17} />}{editingId ? 'Save changes' : 'Create evolution'}</button>
            </div>
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
                <button className="icon-button" title="Edit evolution" disabled={busy || isActive} onClick={beginEdit}><FilePenLine size={17} /></button>
                <button className="icon-button delete-button" title="Delete evolution" disabled={busy || isActive} onClick={() => void deleteEvolution()}><Trash2 size={17} /></button>
                {!isActive && selected.status !== 'completed' && <button className="primary" disabled={busy} onClick={() => void sendCommand('start')}><Play size={16} />Start</button>}
                {selected.status === 'running' && <button className="danger" disabled={busy} onClick={() => void sendCommand('stop')}><CircleStop size={16} />Stop</button>}
              </div>
            </div>

            {isActive && <div className="active-work"><span className="pulse" /><div><small>{selected.status === 'stopRequested' ? 'Stopping after current event' : `Still working · ${formatHeartbeat(activeEvent?.startedAt, now)} in this step`}</small><strong>{activeEvent?.type.replaceAll('.', ' ') ?? 'Preparing next event'}</strong><p>{activeEvent?.prompt ?? 'Waiting for the next persisted event.'}</p></div><div className="running-time"><Timer size={16} />{formatDuration(selected.startedAt, undefined, now)}</div></div>}

            <div className="metrics">
              <div><span>Target</span><strong><GitBranch size={16} />{selected.targetBranch}</strong></div>
              <div><span>Work items</span><strong>{selected.workItems.length} / 5</strong></div>
              <div><span>Events</span><strong>{selected.events.length}</strong></div>
              <div><span>Elapsed</span><strong><Timer size={16} />{formatDuration(selected.startedAt, selected.completedAt, now)}</strong></div>
            </div>

            {selected.error && <div className="error-banner inline"><XCircle size={18} />{selected.error}</div>}
            <div className="content-grid">
              <section className="panel">
                <div className="agent-roster">
                  <div className="roster-title"><Bot size={17} /><h3>Agent team</h3></div>
                  <div className="roster-grid">
                    {agentMembers.map((member) => {
                      const agent = getAgentState(selected.events, member)
                      return <div className={`agent-member ${agent.state}`} key={member.startEvent}><span className="agent-status" /><div><strong>{member.name}</strong><small>{agent.eventType.replaceAll('.', ' ')} · {agent.state}</small></div></div>
                    })}
                  </div>
                </div>
                <div className="section-title"><Check size={17} /><h3>Agent day plan</h3></div>
                {selected.workItems.length === 0 ? <p className="empty">Plan appears after the scan completes.</p> : selected.workItems.map((item, index) => <div className="work-item" key={item.id}><span>{index + 1}</span><div><strong>{item.title}</strong><small>{item.status}</small><p>{item.description}</p>{item.result && <pre>{item.result}</pre>}</div></div>)}
              </section>
              <section className="panel timeline-panel">
                <div className="section-title"><Clock3 size={17} /><h3>Event timeline</h3></div>
                <div className="timeline">
                  {[...selected.events].reverse().map((item) => <div className={`event ${item.status}`} key={item.id}><span className="event-icon">{item.status === 'completed' ? <Check size={13} /> : item.status === 'failed' ? <XCircle size={13} /> : <Clock3 size={13} />}</span><details open><summary><strong>{item.type.replaceAll('.', ' ')}</strong><small>{item.status} · {formatTimestamp(item.startedAt ?? item.createdAt)}</small></summary><div className="event-detail"><dl><div><dt>Created</dt><dd>{formatTimestamp(item.createdAt)}</dd></div><div><dt>Started</dt><dd>{formatTimestamp(item.startedAt)}</dd></div><div><dt>Completed</dt><dd>{formatTimestamp(item.completedAt)}</dd></div></dl>{item.prompt && <><h4>GitHub Copilot prompt</h4><pre>{item.prompt}</pre></>}{item.logs.length > 0 && <><h4>Logs</h4><pre>{item.logs.join('\n\n')}</pre></>}{item.detail && <><h4>Result</h4><pre>{item.detail}</pre></>}</div></details></div>)}
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
