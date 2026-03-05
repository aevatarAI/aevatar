import { useState, useCallback } from 'react'
import { Activity, FileDown, PanelRightOpen, PanelRightClose, Play, Square } from 'lucide-react'
import { useResearchStream } from './hooks/use-research-stream'
import { exportPaper } from './api'
import ResearchStream from './components/ResearchStream'
import GraphView from './components/GraphView'
import aevatarLogo from './assets/aevatar_ai_logo.svg'

export default function App() {
  const [panelOpen, setPanelOpen] = useState(true)
  const [exporting, setExporting] = useState(false)

  const { rounds, runStatus, currentRound, totalBlueNodes, error, llmStreamText, startRun, stopRun } =
    useResearchStream()

  const handleExportPdf = useCallback(async () => {
    setExporting(true)
    try {
      const blob = await exportPaper()
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = 'paper.pdf'
      a.click()
      URL.revokeObjectURL(url)
    } catch (err) {
      alert(err instanceof Error ? err.message : 'PDF export failed')
    } finally {
      setExporting(false)
    }
  }, [])

  const isRunning = runStatus === 'running'

  const statusColor = {
    idle: 'var(--text-dimmed)',
    running: 'var(--text-primary)',
    completed: 'var(--accent-green)',
    error: 'var(--accent-red)',
  }[runStatus]

  const statusLabel = {
    idle: 'IDLE',
    running: 'RUNNING',
    completed: 'COMPLETED',
    error: 'ERROR',
  }[runStatus]

  return (
    <div
      className="h-screen w-screen overflow-hidden flex flex-col"
      style={{ background: 'var(--bg-base)' }}
    >
      {/* Header */}
      <header
        className="flex items-center justify-between px-6 py-4 relative z-20"
        style={{ background: 'var(--bg-surface)' }}
      >
        <div className="flex items-center gap-5">
          <img src={aevatarLogo} alt="aevatar.ai" className="h-6" />
          <div
            className="h-5 w-px"
            style={{
              background:
                'linear-gradient(180deg, transparent, var(--border-strong), transparent)',
            }}
          />
          <span
            className="text-sm font-semibold"
            style={{ color: 'var(--text-muted)' }}
          >
            Sisyphus
          </span>
        </div>
        <div className="flex items-center gap-3">
          {/* PDF export */}
          <button
            onClick={handleExportPdf}
            disabled={exporting || isRunning}
            className="btn-secondary text-xs gap-1.5 py-1.5 px-3 disabled:opacity-40 disabled:cursor-not-allowed"
          >
            <FileDown size={14} />
            {exporting ? 'Exporting...' : 'Export PDF'}
          </button>
          {/* Start / Stop */}
          {isRunning ? (
            <button onClick={stopRun} className="btn-warning text-xs gap-1.5 py-1.5 px-3">
              <Square size={14} />
              Stop
            </button>
          ) : (
            <button onClick={startRun} className="btn-primary text-xs gap-1.5 py-1.5 px-3">
              <Play size={14} />
              Start
            </button>
          )}
          {/* Toggle research panel */}
          <button
            onClick={() => setPanelOpen(!panelOpen)}
            className="icon-btn"
            title={panelOpen ? 'Hide research panel' : 'Show research panel'}
            style={{ background: 'var(--bg-elevated)', border: '1px solid var(--border-subtle)' }}
          >
            {panelOpen ? <PanelRightClose size={16} /> : <PanelRightOpen size={16} />}
          </button>
          {/* Status badge */}
          <Activity size={14} style={{ color: statusColor }} />
          <span
            className="badge text-[10px]"
            style={{ color: statusColor, borderColor: statusColor }}
          >
            {statusLabel}
          </span>
        </div>
      </header>
      <div className="divider-h" />

      {/* Main content: Graph + collapsible Research panel */}
      <main className="flex-1 flex min-h-0 min-w-0 overflow-hidden">
        {/* Graph (fills remaining space) */}
        <GraphView runStatus={runStatus} />

        {/* Research side panel */}
        {panelOpen && (
          <>
            <div
              className="w-px shrink-0"
              style={{ background: 'var(--border-default)' }}
            />
            <div
              className="shrink-0 flex flex-col overflow-hidden"
              style={{
                width: 380,
                background: 'var(--bg-surface)',
              }}
            >
              <ResearchStream
                rounds={rounds}
                runStatus={runStatus}
                currentRound={currentRound}
                totalBlueNodes={totalBlueNodes}
                error={error}
                llmStreamText={llmStreamText}
              />
            </div>
          </>
        )}
      </main>
    </div>
  )
}
