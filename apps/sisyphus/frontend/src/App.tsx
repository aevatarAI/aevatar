import { useState, useCallback } from 'react'
import { Activity, FileDown } from 'lucide-react'
import { useResearchStream } from './hooks/use-research-stream'
import { exportPaper } from './api'
import ResearchStream from './components/ResearchStream'
import InputBar from './components/InputBar'
import GraphView from './components/GraphView'
import aevatarLogo from './assets/aevatar_ai_logo.svg'

export default function App() {
  const [activeTab, setActiveTab] = useState<'research' | 'graph'>('research')
  const [exporting, setExporting] = useState(false)

  const { rounds, runStatus, currentRound, totalBlueNodes, error, startRun, stopRun } =
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
            disabled={exporting || runStatus === 'running'}
            className="btn-secondary text-xs gap-1.5 py-1.5 px-3 disabled:opacity-40 disabled:cursor-not-allowed"
          >
            <FileDown size={14} />
            {exporting ? 'Exporting...' : 'Export PDF'}
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

      {/* Main content */}
      <main className="flex-1 flex flex-col min-h-0 min-w-0 overflow-hidden">
        {/* Tab bar */}
        <div
          className="flex shrink-0"
          style={{ borderBottom: '1px solid var(--border-subtle)' }}
        >
          {(['research', 'graph'] as const).map((tab) => (
            <button
              key={tab}
              onClick={() => setActiveTab(tab)}
              className="px-4 py-2.5 text-xs font-medium uppercase tracking-wider transition-colors relative"
              style={{
                color:
                  activeTab === tab
                    ? 'var(--text-primary)'
                    : 'var(--text-dimmed)',
              }}
            >
              {tab}
              {activeTab === tab && (
                <div
                  className="absolute bottom-0 left-0 right-0 h-[2px]"
                  style={{ background: 'var(--text-primary)' }}
                />
              )}
            </button>
          ))}
        </div>

        {/* Tab content */}
        {activeTab === 'research' ? (
          <div className="flex-1 flex flex-col min-h-0 overflow-hidden">
            <ResearchStream
              rounds={rounds}
              runStatus={runStatus}
              currentRound={currentRound}
              totalBlueNodes={totalBlueNodes}
              error={error}
            />
            <div className="divider-h" />
            <InputBar
              runStatus={runStatus}
              onStart={startRun}
              onStop={stopRun}
            />
          </div>
        ) : (
          <GraphView runStatus={runStatus} />
        )}
      </main>
    </div>
  )
}
