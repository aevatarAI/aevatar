import { useState, useCallback } from 'react'
import { Activity, FileDown, Play, Square, ChevronLeft, ChevronRight, Loader2, X } from 'lucide-react'
import { useResearchStream } from './hooks/use-research-stream'
import { exportPaper } from './api'
import ResearchStream from './components/ResearchStream'
import GraphView from './components/GraphView'
import aevatarLogo from './assets/aevatar_ai_logo.svg'

export default function App() {
  const [panelOpen, setPanelOpen] = useState(false)
  const [exporting, setExporting] = useState(false)

  const { rounds, runStatus, currentRound, totalBlueNodes, error, llmStreamText, startRun, stopRun } =
    useResearchStream()

  const [exportError, setExportError] = useState<string | null>(null)

  const handleExportPdf = useCallback(async () => {
    setExporting(true)
    setExportError(null)
    try {
      const blob = await exportPaper()
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = 'paper.pdf'
      a.click()
      URL.revokeObjectURL(url)
    } catch (err) {
      setExportError(err instanceof Error ? err.message : 'PDF export failed')
    } finally {
      setExporting(false)
    }
  }, [])

  const isRunning = runStatus === 'running'

  const statusColor = {
    idle: 'var(--text-dimmed)',
    running: '#00ffff',
    completed: 'var(--text-dimmed)',
    error: '#ff2e2e',
  }[runStatus]

  const statusLabel = {
    idle: 'IDLE',
    running: 'RUNNING',
    completed: 'IDLE',
    error: 'ERROR',
  }[runStatus]

  return (
    <div
      className="h-screen w-screen overflow-hidden relative"
      style={{ background: 'var(--bg-base)' }}
    >
      {/* Graph (full screen, behind everything) */}
      <div className="absolute inset-0">
        <GraphView />
      </div>

      {/* Glassmorphism header */}
      <header className="glass-header absolute top-0 left-0 right-0 z-20 flex items-center justify-between px-6 py-4">
        <div className="flex items-center gap-5">
          <img src={aevatarLogo} alt="aevatar.ai" className="h-6" />
          <div
            className="h-5 w-px"
            style={{
              background:
                'linear-gradient(180deg, transparent, rgba(0,255,255,0.3), transparent)',
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
          {/* Download Paper */}
          <button
            onClick={handleExportPdf}
            disabled={exporting || isRunning}
            className="btn-neon-blue text-xs gap-1.5 py-1.5 px-3 disabled:opacity-30 disabled:cursor-not-allowed"
          >
            <FileDown size={14} />
            Download Paper
          </button>
          {/* Start / Stop */}
          {isRunning ? (
            <button onClick={stopRun} className="btn-neon-danger text-xs gap-1.5 py-1.5 px-3">
              <Square size={14} />
              Stop
            </button>
          ) : (
            <button onClick={startRun} className="btn-neon-green text-xs gap-1.5 py-1.5 px-3">
              <Play size={14} />
              Start
            </button>
          )}
          {/* Status badge */}
          <Activity size={14} style={{ color: statusColor }} />
          <span
            className={`badge text-[10px] ${isRunning ? 'badge-neon-pulse' : ''}`}
            style={{
              color: statusColor,
              borderColor: statusColor,
              textShadow: isRunning ? `0 0 8px ${statusColor}` : 'none',
            }}
          >
            {statusLabel}
          </span>
        </div>
      </header>

      {/* Research panel (slides in/out via transform) */}
      <div
        className="absolute top-0 right-0 bottom-0 z-10 glass-panel flex flex-col overflow-hidden"
        style={{
          transform: panelOpen ? 'translateX(0)' : 'translateX(100%)',
          transition: 'transform 0.35s cubic-bezier(0.16, 1, 0.3, 1)',
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

      {/* Floating panel toggle button */}
      <button
        onClick={() => setPanelOpen(!panelOpen)}
        className="panel-toggle group"
        style={{
          right: panelOpen ? 'calc(66vw + 12px)' : '12px',
          transition: 'right 0.35s cubic-bezier(0.16, 1, 0.3, 1)',
        }}
        title={panelOpen ? 'Hide research panel' : 'Show research panel'}
      >
        {panelOpen ? (
          <ChevronRight size={12} className="text-[var(--text-dimmed)] group-hover:text-[#00ffff] transition-colors" />
        ) : (
          <ChevronLeft size={12} className="text-[var(--text-dimmed)] group-hover:text-[#00ffff] transition-colors" />
        )}
      </button>

      {/* Export popup overlay */}
      {(exporting || exportError) && (
        <div className="fixed inset-0 z-50 flex items-center justify-center" style={{ background: 'rgba(0,0,0,0.6)', backdropFilter: 'blur(4px)' }}>
          <div className="cyber-popup">
            <div className="cyber-popup-border" />
            {exporting ? (
              <div className="flex flex-col items-center gap-4 py-2">
                <Loader2 size={28} className="animate-spin" style={{ color: '#4488ff', filter: 'drop-shadow(0 0 8px rgba(68,136,255,0.5))' }} />
                <span className="text-sm font-medium" style={{ color: '#4488ff', textShadow: '0 0 10px rgba(68,136,255,0.3)' }}>
                  Paper generation in progress
                </span>
                <div className="thinking-dots" style={{ '--dot-color': '#4488ff' } as React.CSSProperties}>
                  <span />
                  <span />
                  <span />
                </div>
              </div>
            ) : exportError ? (
              <div className="flex flex-col items-center gap-3 py-2">
                <span className="text-sm" style={{ color: '#ff4444', textShadow: '0 0 8px rgba(255,68,68,0.3)' }}>
                  {exportError}
                </span>
                <button
                  onClick={() => setExportError(null)}
                  className="btn-neon-danger text-xs py-1 px-3 gap-1"
                >
                  <X size={12} />
                  Close
                </button>
              </div>
            ) : null}
          </div>
        </div>
      )}
    </div>
  )
}
