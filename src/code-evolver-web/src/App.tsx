import { useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import { Activity, ArrowRight, Bot, Check, CircleStop, Clock3, Download, FilePenLine, FileUp, FolderOpen, GitBranch, Play, Plus, RefreshCw, Save, Search, Server, Sparkles, Timer, Trash2, X, XCircle } from 'lucide-react'
import './App.css'

type EvolutionStatus = 'draft' | 'running' | 'stopRequested' | 'stopped' | 'completed' | 'failed'
type EventStatus = 'pending' | 'processing' | 'completed' | 'failed' | 'cancelled'
type WorkItem = { id: string; title: string; description: string; status: string; result?: string }
type EvolutionEvent = { id: string; type: string; status: EventStatus; detail?: string; prompt?: string; logs: string[]; createdAt: string; startedAt?: string; completedAt?: string }
type AgentState = 'working' | 'done' | 'queued' | 'waiting' | 'stopped' | 'failed'
type AgentMember = { name: string; startEvent: string; completedEvent: string }
type DataAnalysis = { id: string; fileName: string; status: 'running' | 'completed' | 'failed'; direction?: string; keyPoints: string[]; error?: string; events: EvolutionEvent[] }
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
const repositoryStorageKey = 'code-evolver.repository-path'
const emptyForm = { repositoryPath: '', direction: 'Improve e2e user experience & design and fix functionality issues.', scope: '.', targetBranch: 'main' }
const directionOptions = [
  'Improve e2e user experience & design and fix functionality issues.',
  'Improve API reliability and test coverage',
  'Refactor for maintainability without behavior changes',
]
const scopeOptions = ['.', 'src', 'src/code-evolver-web/src', 'tests']
const branchOptions = ['main', 'develop', 'release']
const agentMembers: AgentMember[] = [
  { name: 'Scan agent', startEvent: 'scan.started', completedEvent: 'scan.completed' },
  { name: 'Plan agent', startEvent: 'plan.started', completedEvent: 'plan.completed' },
  { name: 'Worker agent', startEvent: 'work-item.started', completedEvent: 'work-item.completed' },
  { name: 'Reviewer agent', startEvent: 'review.started', completedEvent: 'review.completed' },
  { name: 'Gate agent', startEvent: 'gate.started', completedEvent: 'gate.completed' },
  { name: 'Merge agent', startEvent: 'change.merged', completedEvent: 'change.merged' },
]
const dataAgentMembers: AgentMember[] = [
  { name: 'Purpose agent', startEvent: 'data-purpose.started', completedEvent: 'data-purpose.started' },
  { name: 'Insight agent', startEvent: 'data-insight.started', completedEvent: 'data-insight.started' },
  { name: 'Direction agent', startEvent: 'evolution-direction.started', completedEvent: 'evolution-direction.started' },
  { name: 'Summary agent', startEvent: 'analysis-summary.started', completedEvent: 'analysis-summary.started' },
]

const getAgentState = (events: EvolutionEvent[], member: AgentMember): { state: AgentState; eventType: string } => {
  const started = [...events].reverse().find((event) => event.type === member.startEvent)
  const completed = [...events].reverse().find((event) => event.type === member.completedEvent && event.status === 'completed')
  if (started?.status === 'processing') return { state: 'working', eventType: started.type }
  if (started?.status === 'failed') return { state: 'failed', eventType: started.type }
  if (started?.status === 'cancelled') return { state: 'stopped', eventType: started.type }
  if (started?.status === 'pending') return { state: 'queued', eventType: started.type }
  if (completed || started?.status === 'completed') return { state: 'done', eventType: completed?.type ?? started?.type ?? member.completedEvent }
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

const filterEventsByLogs = (events: EvolutionEvent[], logSearch: string) => {
  const query = logSearch.trim().toLocaleLowerCase()
  if (!query) return events
  return events
    .map((event) => ({
      ...event,
      logs: event.logs.filter((log) => log.toLocaleLowerCase().includes(query)),
    }))
    .filter((event) => event.logs.length > 0)
}

const normalizeCsvCell = (value: string) => value.replaceAll('\r\n', '\n').replaceAll('\r', '\n')
const protectCsvCell = (value: string) => /^[\s]*[=+\-@]/.test(value) ? `'${value}` : value
const escapeCsvCell = (value: string) => `"${protectCsvCell(normalizeCsvCell(value)).replaceAll('"', '""')}"`

const buildEventLogsCsv = (events: EvolutionEvent[]) => {
  const rows = [['eventId', 'type', 'status', 'createdAt', 'startedAt', 'completedAt', 'log']]
  events.forEach((event) => {
    event.logs.forEach((log) => rows.push([event.id, event.type, event.status, event.createdAt, event.startedAt ?? '', event.completedAt ?? '', log]))
  })
  return rows.map((row) => row.map(escapeCsvCell).join(',')).join('\r\n')
}

const AgentRoster = ({ members, events }: { members: AgentMember[]; events: EvolutionEvent[] }) => <div className="roster-grid">
  {members.map((member) => {
    const agent = getAgentState(events, member)
    return <div className={`agent-member ${agent.state}`} key={member.startEvent}><span className="agent-status" /><div><strong>{member.name}</strong><small>{agent.eventType.replaceAll('.', ' ')} · {agent.state}</small></div></div>
  })}
</div>

const EventTimeline = ({ events }: { events: EvolutionEvent[] }) => <div className="timeline">
  {[...events].reverse().map((item) => <div className={`event ${item.status}`} key={item.id}><span className="event-icon">{item.status === 'completed' ? <Check size={13} /> : item.status === 'failed' ? <XCircle size={13} /> : <Clock3 size={13} />}</span><details open><summary><strong>{item.type.replaceAll('.', ' ')}</strong><small>{item.status} · {formatTimestamp(item.startedAt ?? item.createdAt)}</small></summary><div className="event-detail"><dl><div><dt>Created</dt><dd>{formatTimestamp(item.createdAt)}</dd></div><div><dt>Started</dt><dd>{formatTimestamp(item.startedAt)}</dd></div><div><dt>Completed</dt><dd>{formatTimestamp(item.completedAt)}</dd></div></dl>{item.prompt && <><h4>GitHub Copilot prompt</h4><pre>{item.prompt}</pre></>}{item.logs.length > 0 && <><h4>Logs</h4><pre>{item.logs.join('\n\n')}</pre></>}{item.detail && <><h4>Result</h4><pre>{item.detail}</pre></>}</div></details></div>)}
  {events.length === 0 && <p className="empty">No event logs match the current search.</p>}
</div>

function App() {
  const [evolutions, setEvolutions] = useState<Evolution[]>([])
  const [selectedId, setSelectedId] = useState<string>()
  const [form, setForm] = useState(() => ({ ...emptyForm, repositoryPath: window.localStorage.getItem(repositoryStorageKey) ?? '' }))
  const [editingId, setEditingId] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [selectingRepository, setSelectingRepository] = useState(false)
  const [dataFile, setDataFile] = useState<File>()
  const [analyzingData, setAnalyzingData] = useState(false)
  const [dataAnalysis, setDataAnalysis] = useState<DataAnalysis>()
  const [logSearch, setLogSearch] = useState('')
  const [error, setError] = useState('')
  const [now, setNow] = useState(0)
  const selected = evolutions.find((item) => item.id === selectedId) ?? evolutions[0]
  const isActive = selected?.status === 'running' || selected?.status === 'stopRequested'
  const activeEvent = selected?.events.find((item) => item.status === 'processing')
  const latestActivity = activeEvent?.logs.at(-1) ?? activeEvent?.prompt ?? 'Waiting for the next persisted event.'
  const filteredSelectedEvents = useMemo(() => filterEventsByLogs(selected?.events ?? [], logSearch), [selected?.events, logSearch])

  const refresh = async () => {
    try {
      const response = await fetch(`${apiUrl}/evolutions`)
      if (!response.ok) throw new Error(`API returned ${response.status}`)
      const data: Evolution[] = await response.json()
      setEvolutions(data)
      setSelectedId((current) => (current && data.some((item) => item.id === current) ? current : data[0]?.id))
      setError('')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not reach the API.')
    }
  }

  const setRepositoryPath = (repositoryPath: string) => {
    setForm((current) => ({ ...current, repositoryPath }))
    window.localStorage.setItem(repositoryStorageKey, repositoryPath)
  }

  const selectRepository = async () => {
    setSelectingRepository(true)
    try {
      const response = await fetch(`${apiUrl}/repository/select`, { method: 'POST' })
      if (response.status === 204) return
      if (!response.ok) throw new Error(await readError(response, 'Could not open the repository picker.'))
      const data: { repositoryPath: string } = await response.json()
      setRepositoryPath(data.repositoryPath)
      setError('')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not select the repository.')
    } finally {
      setSelectingRepository(false)
    }
  }

  const analyzeData = async () => {
    if (!dataFile) return
    setAnalyzingData(true)
    setDataAnalysis(undefined)
    try {
      const body = new FormData()
      body.append('file', dataFile)
      body.append('repositoryPath', form.repositoryPath)
      const response = await fetch(`${apiUrl}/data-analysis`, { method: 'POST', body })
      if (!response.ok) throw new Error(await readError(response, 'Could not analyze the data file.'))
      setDataAnalysis(await response.json() as DataAnalysis)
      setError('')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not analyze the data file.')
    } finally {
      setAnalyzingData(false)
    }
  }

  const applyDataAnalysis = () => {
    if (!dataAnalysis?.direction) return
    const keyPoints = dataAnalysis.keyPoints.map((point) => `- ${point}`).join('\n')
    setForm((current) => ({ ...current, direction: `${dataAnalysis.direction}\n\nKey points:\n${keyPoints}` }))
  }

  const analysisId = dataAnalysis?.id
  const analysisStatus = dataAnalysis?.status
  useEffect(() => {
    if (!analysisId || analysisStatus !== 'running') return
    const refreshAnalysis = async () => {
      const response = await fetch(`${apiUrl}/data-analysis/${analysisId}`)
      if (response.ok) setDataAnalysis(await response.json() as DataAnalysis)
    }
    const timer = window.setInterval(() => void refreshAnalysis(), 1000)
    return () => window.clearInterval(timer)
  }, [analysisId, analysisStatus])

  useEffect(() => {
    void refresh()
    void fetch(`${apiUrl}/repository`)
      .then(async (response) => {
        if (!response.ok) return
        const data: { repositoryPath: string } = await response.json()
        setForm((current) => {
          if (current.repositoryPath) return current
          window.localStorage.setItem(repositoryStorageKey, data.repositoryPath)
          return { ...current, repositoryPath: data.repositoryPath }
        })
      })
      .catch(() => undefined)
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

  const selectEvolution = (evolution: Evolution) => {
    setSelectedId(evolution.id)
    setEditingId(undefined)
    setForm({
      repositoryPath: evolution.repositoryPath,
      direction: evolution.direction,
      scope: evolution.scope,
      targetBranch: evolution.targetBranch,
    })
    window.localStorage.setItem(repositoryStorageKey, evolution.repositoryPath)
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

  const exportFilteredLogs = () => {
    if (!selected) return
    const csv = buildEventLogsCsv(filteredSelectedEvents)
    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = `code-evolver-${selected.id}-logs.csv`
    link.click()
    URL.revokeObjectURL(url)
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
        <section className="workspace-row analyzer-workspace">
        <aside className="workspace-sidebar">
          <section className="data-analyzer">
            <div className="area-heading"><span>Analyzer</span><strong>Prepare an evolution from data</strong></div>
            <div className="section-title"><Sparkles size={17} /><h2>Data Analyzer</h2></div>
            <label className="data-upload">
              <FileUp size={18} />
              <span><strong>{dataFile?.name ?? 'Select data file'}</strong><small>CSV or JSON, up to 5 MB</small></span>
              <input type="file" accept=".csv,.json,application/json,text/csv" onChange={(event) => { setDataFile(event.target.files?.[0]); setDataAnalysis(undefined) }} />
            </label>
            <button type="button" className="secondary analyze-button" disabled={!dataFile || !form.repositoryPath || analyzingData || dataAnalysis?.status === 'running'} onClick={() => void analyzeData()}><Sparkles size={16} />{analyzingData || dataAnalysis?.status === 'running' ? 'Agent team analyzing...' : 'Analyze data'}</button>
            {dataAnalysis?.status === 'failed' && <div className="analysis-error"><XCircle size={15} />{dataAnalysis.error}</div>}
            {dataAnalysis?.status === 'completed' && dataAnalysis.direction && <div className="analysis-result"><strong>{dataAnalysis.direction}</strong><ul>{dataAnalysis.keyPoints.map((point) => <li key={point}>{point}</li>)}</ul><button type="button" className="primary" onClick={applyDataAnalysis}><ArrowRight size={16} />Apply to evolution</button></div>}
          </section>
        </aside>
        <section className="detail analyzer-detail">
          <div className="content-grid">
            <section className="panel">
              <div className="roster-title"><Sparkles size={17} /><h3>Analyzer Team</h3>{dataAnalysis && <span className={`analysis-status ${dataAnalysis.status}`}>{dataAnalysis.status}</span>}</div>
              <AgentRoster members={dataAgentMembers} events={dataAnalysis?.events ?? []} />
            </section>
            <section className="panel timeline-panel">
              <div className="section-title"><Clock3 size={17} /><h3>Analysis Timeline</h3></div>
              {dataAnalysis ? <EventTimeline events={dataAnalysis.events} /> : <p className="empty">Analysis events will appear after a data file is submitted.</p>}
            </section>
          </div>
        </section>
        </section>

        <section className="workspace-row evolution-workspace">
        <aside className="workspace-sidebar">
          <form onSubmit={createEvolution}>
            <div className="area-heading"><span>Evolution</span><strong>Define and run the code change</strong></div>
            <div className="section-title">{editingId ? <FilePenLine size={17} /> : <Plus size={17} />}<h2>{editingId ? 'Edit evolution' : 'New evolution'}</h2></div>
            <label>Cloned repository path<div className="path-input"><input required value={form.repositoryPath} onChange={(e) => setRepositoryPath(e.target.value)} placeholder="C:\work\repository" /><button type="button" className="icon-button" title="Select repository folder" disabled={selectingRepository} onClick={() => void selectRepository()}><FolderOpen size={17} /></button></div></label>
            <label>Direction preset<select value={directionOptions.includes(form.direction) ? form.direction : ''} onChange={(event) => { if (event.target.value) setForm({ ...form, direction: event.target.value }) }}><option value="">Custom direction</option>{directionOptions.map((option) => <option key={option} value={option}>{option}</option>)}</select></label>
            <label>Evolution direction<textarea required rows={4} value={form.direction} onChange={(e) => setForm({ ...form, direction: e.target.value })} placeholder="Improve API reliability and test coverage" /></label>
            <label>Scope<select value={scopeOptions.includes(form.scope) ? form.scope : ''} onChange={(event) => { if (event.target.value) setForm({ ...form, scope: event.target.value }) }}><option value="">Custom scope</option>{scopeOptions.map((option) => <option key={option} value={option}>{option}</option>)}</select><input required value={form.scope} onChange={(e) => setForm({ ...form, scope: e.target.value })} placeholder="src/api" aria-label="Custom scope" /></label>
            <label>Target branch<div className="selection-with-custom"><select value={branchOptions.includes(form.targetBranch) ? form.targetBranch : ''} onChange={(event) => { if (event.target.value) setForm({ ...form, targetBranch: event.target.value }) }} aria-label="Known target branch"><option value="">Custom branch</option>{branchOptions.map((option) => <option key={option} value={option}>{option}</option>)}</select><div className="input-icon"><GitBranch size={16} /><input required value={form.targetBranch} onChange={(e) => setForm({ ...form, targetBranch: e.target.value })} aria-label="Custom target branch" /></div></div></label>
            <div className="form-actions">
              {editingId && <button type="button" className="secondary" disabled={busy} onClick={cancelEdit}><X size={17} />Cancel</button>}
              <button className="primary" disabled={busy}>{editingId ? <Save size={17} /> : <Plus size={17} />}{editingId ? 'Save changes' : 'Create evolution'}</button>
            </div>
          </form>

          <div className="evolution-list">
            <div className="section-title"><Server size={17} /><h2>Evolutions</h2><span>{evolutions.length}</span></div>
            {evolutions.length === 0 && <p className="empty">No evolutions yet.</p>}
            {evolutions.map((item) => (
              <button key={item.id} className={`evolution-row ${selected?.id === item.id ? 'selected' : ''}`} onClick={() => selectEvolution(item)}>
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
              <div><span className={`status-label ${selected.status}`}>{selected.status}</span><h2 tabIndex={0}>{selected.direction}</h2><p>{selected.repositoryPath} <span>/</span> {selected.scope}</p></div>
              <div className="actions">
                <button className="icon-button" title="Refresh" onClick={() => void refresh()}><RefreshCw size={17} /></button>
                <button className="icon-button" title="Edit evolution" disabled={busy || isActive} onClick={beginEdit}><FilePenLine size={17} /></button>
                <button className="icon-button delete-button" title="Delete evolution" disabled={busy || isActive} onClick={() => void deleteEvolution()}><Trash2 size={17} /></button>
                {!isActive && selected.status !== 'completed' && <button className="primary" disabled={busy} onClick={() => void sendCommand('start')}><Play size={16} />Start</button>}
                {selected.status === 'running' && <button className="danger" disabled={busy} onClick={() => void sendCommand('stop')}><CircleStop size={16} />Stop</button>}
              </div>
            </div>

            {isActive && <div className="active-work"><span className="pulse" /><div><small>{selected.status === 'stopRequested' ? 'Stopping after current event' : `Live activity · ${formatHeartbeat(activeEvent?.startedAt, now)} in this step`}</small><strong>{activeEvent?.type.replaceAll('.', ' ') ?? 'Preparing next event'}</strong><p>{latestActivity}</p></div><div className="running-time"><Timer size={16} />{formatDuration(selected.startedAt, undefined, now)}</div></div>}

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
                  <AgentRoster members={agentMembers} events={selected.events} />
                </div>
                <div className="section-title"><Check size={17} /><h3>Agent day plan</h3></div>
                {selected.workItems.length === 0 ? <p className="empty">The plan will appear when the planning agent completes.</p> : selected.workItems.map((item, index) => <div className="work-item" key={item.id}><span>{index + 1}</span><div><strong>{item.title}</strong><small>{item.status}</small><p>{item.description}</p>{item.result && <pre>{item.result}</pre>}</div></div>)}
              </section>
              <section className="panel timeline-panel">
                <div className="section-title"><Clock3 size={17} /><h3>Event timeline</h3></div>
                <div className="log-tools">
                  <label><Search size={15} />Search event logs<input value={logSearch} onChange={(event) => setLogSearch(event.target.value)} placeholder="Filter by actual log text" /></label>
                  <button type="button" className="secondary" disabled={filteredSelectedEvents.every((event) => event.logs.length === 0)} onClick={exportFilteredLogs}><Download size={16} />Export CSV</button>
                  <small>{filteredSelectedEvents.length} of {selected.events.length} events</small>
                </div>
                <EventTimeline events={filteredSelectedEvents} />
              </section>
            </div>
          </>}
        </section>
        </section>
      </div>
    </main>
  )
}

export default App
