import React from 'react'
import { createRoot } from 'react-dom/client'
import './styles.css'

type ActorGroup = {
  type: string
  actorIds: string[]
  count: number
}

type ActorItem = {
  actorId: string
  type: string
}

type WorkflowRun = {
  actorId: string
  workflowName: string
  status: string
  totalSteps: number
  completedSteps: number
}

type ReadModelSummary = {
  name: string
  latestStateVersion?: number
}

type TelemetryFrame = {
  name: string
  traceId: string
  tags?: Record<string, string>
}

function App() {
  const [actors, setActors] = React.useState<ActorItem[]>([])
  const [workflows, setWorkflows] = React.useState<WorkflowRun[]>([])
  const [readmodels, setReadmodels] = React.useState<ReadModelSummary[]>([])
  const [events, setEvents] = React.useState<TelemetryFrame[]>([])
  const [filter, setFilter] = React.useState('')
  const [selected, setSelected] = React.useState<string | null>(null)
  const [live, setLive] = React.useState(false)

  const refresh = React.useCallback(async () => {
    const [actorResponse, workflowResponse, readmodelResponse] = await Promise.all([
      fetch('/api/inspector/actors').then((r) => r.json()),
      fetch('/api/inspector/workflow-runs').then((r) => r.json()),
      fetch('/api/inspector/readmodels').then((r) => r.json()),
    ])
    setActors((actorResponse.groups as ActorGroup[] ?? []).flatMap((group) =>
      group.actorIds.map((actorId) => ({ actorId, type: group.type }))
    ))
    setWorkflows(workflowResponse)
    setReadmodels(readmodelResponse)
  }, [])

  React.useEffect(() => {
    refresh()
    const interval = window.setInterval(refresh, 5000)
    const source = new EventSource('/api/inspector/events')
    source.onopen = () => setLive(true)
    source.onerror = () => setLive(false)
    source.addEventListener('activity', (event) => {
      setEvents((current) => [JSON.parse(event.data), ...current].slice(0, 24))
    })
    return () => {
      window.clearInterval(interval)
      source.close()
    }
  }, [refresh])

  const visible = actors.filter((actor) =>
    actor.actorId.toLowerCase().includes(filter.toLowerCase()) ||
    actor.type.toLowerCase().includes(filter.toLowerCase()))
  const selectedActor = actors.find((actor) => actor.actorId === selected)

  return (
    <div className="app">
      <header className="topbar">
        <div>
          <h1>Aevatar Inspector</h1>
          <p className={live ? 'ok' : 'bad'}>{live ? 'Live' : 'Disconnected'}</p>
        </div>
        <input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="agent type or id" />
        <button type="button" onClick={async () => { await fetch('/api/inspector/demo/hierarchy', { method: 'POST' }); await refresh() }}>Run hierarchy</button>
      </header>
      <main className="grid">
        <aside>
          <div className="label">Actors</div>
          {visible.map((actor) => (
            <button key={actor.actorId} type="button" className="actor" onClick={() => setSelected(actor.actorId)}>
              <span>{actor.actorId}</span><span>{actor.type}</span>
            </button>
          ))}
        </aside>
        <section className="canvas">
          {visible.map((actor, index) => (
            <div
              key={actor.actorId}
              className="node"
              style={{ left: `${16 + (index % 4) * 21}%`, top: `${18 + Math.floor(index / 4) * 18}%` }}
            >
              <span />
              <strong>{actor.actorId}</strong>
            </div>
          ))}
        </section>
        <aside>
          <div className="label">Inspector</div>
          {selectedActor ? <dl><dt>actor</dt><dd>{selectedActor.actorId}</dd><dt>type</dt><dd>{selectedActor.type}</dd></dl> : <p>Select an actor</p>}
          <div className="label">Recent Events</div>
          {events.slice(0, 8).map((event, index) => <p key={`${event.traceId}-${index}`} className="event">{event.name}</p>)}
        </aside>
      </main>
      <footer>
        <section>
          <div className="label">Workflow Runs</div>
          {workflows.map((run) => <p key={run.actorId}>{run.workflowName || 'unknown'} · {run.status}</p>)}
        </section>
        <section>
          <div className="label">ReadModels</div>
          {readmodels.map((model) => <p key={model.name}>{model.name} <b>v{model.latestStateVersion ?? 0}</b></p>)}
        </section>
      </footer>
    </div>
  )
}

createRoot(document.getElementById('root')!).render(<App />)
