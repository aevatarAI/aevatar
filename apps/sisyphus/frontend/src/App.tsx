import { useState, useEffect, useRef, useCallback } from 'react'
import { Activity, ChevronDown } from 'lucide-react'
import clsx from 'clsx'
import { fetchWorkflows, fetchWorkflowYaml } from './api'
import { useResearchStream } from './hooks/use-research-stream'
import YamlViewer from './components/YamlViewer'
import ResearchStream from './components/ResearchStream'
import InputBar from './components/InputBar'
import aevatarLogo from './assets/aevatar_ai_logo.svg'

export default function App() {
  const [workflows, setWorkflows] = useState<string[]>([])
  const [selectedWorkflow, setSelectedWorkflow] = useState<string | null>(null)
  const [workflowYaml, setWorkflowYaml] = useState<string | null>(null)
  const [yamlLoading, setYamlLoading] = useState(false)
  const [dropdownOpen, setDropdownOpen] = useState(false)
  const [leftPanelWidth, setLeftPanelWidth] = useState(35) // percentage
  const draggingRef = useRef(false)
  const containerRef = useRef<HTMLElement>(null)

  const { messages, toolCalls, timeline, runStatus, currentStep, error, iterationCount, startRun, stopRun } = useResearchStream()

  const handleMouseDown = useCallback(() => {
    draggingRef.current = true
    document.body.style.cursor = 'col-resize'
    document.body.style.userSelect = 'none'
  }, [])

  useEffect(() => {
    const handleMouseMove = (e: MouseEvent) => {
      if (!draggingRef.current || !containerRef.current) return
      const rect = containerRef.current.getBoundingClientRect()
      const pct = ((e.clientX - rect.left) / rect.width) * 100
      setLeftPanelWidth(Math.min(60, Math.max(20, pct)))
    }
    const handleMouseUp = () => {
      if (draggingRef.current) {
        draggingRef.current = false
        document.body.style.cursor = ''
        document.body.style.userSelect = ''
      }
    }
    window.addEventListener('mousemove', handleMouseMove)
    window.addEventListener('mouseup', handleMouseUp)
    return () => {
      window.removeEventListener('mousemove', handleMouseMove)
      window.removeEventListener('mouseup', handleMouseUp)
    }
  }, [])

  // Load workflows on mount
  useEffect(() => {
    fetchWorkflows()
      .then((wfs) => {
        setWorkflows(wfs)
        if (wfs.length > 0) setSelectedWorkflow(wfs[0])
      })
      .catch(console.error)
  }, [])

  // Load YAML when workflow changes
  useEffect(() => {
    if (!selectedWorkflow) return
    setYamlLoading(true)
    fetchWorkflowYaml(selectedWorkflow)
      .then(setWorkflowYaml)
      .catch(console.error)
      .finally(() => setYamlLoading(false))
  }, [selectedWorkflow])

  const handleRun = (topic: string, maxIterations: number) => {
    const prompt = `Research Topic: ${topic}\nMax Rounds: ${maxIterations}`
    startRun(prompt, selectedWorkflow ?? undefined)
  }

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
    <div className="h-screen w-screen overflow-hidden flex flex-col" style={{ background: 'var(--bg-base)' }}>
      {/* Header */}
      <header
        className="flex items-center justify-between px-6 py-4 relative z-20"
        style={{ background: 'var(--bg-surface)' }}
      >
        <div className="flex items-center gap-5">
          <img src={aevatarLogo} alt="aevatar.ai" className="h-6" />
          <div className="h-5 w-px" style={{ background: 'linear-gradient(180deg, transparent, var(--border-strong), transparent)' }} />
          <span className="text-sm font-semibold" style={{ color: 'var(--text-muted)' }}>
            Sisyphus
          </span>
          {/* Workflow selector */}
          <div className="relative">
            <button
              onClick={() => setDropdownOpen(!dropdownOpen)}
              className="btn-secondary text-xs gap-1 py-1.5 px-3"
            >
              {selectedWorkflow ?? 'Select workflow'}
              <ChevronDown size={14} />
            </button>
            {dropdownOpen && (
              <div
                className="absolute top-full left-0 mt-1 w-56 py-1 rounded-md z-50"
                style={{ background: 'var(--bg-elevated)', border: '1px solid var(--border-default)' }}
              >
                {workflows.map((wf) => (
                  <button
                    key={wf}
                    onClick={() => { setSelectedWorkflow(wf); setDropdownOpen(false) }}
                    className={clsx(
                      'w-full text-left px-3 py-2 text-xs font-mono transition-colors',
                      wf === selectedWorkflow
                        ? 'text-white bg-[var(--bg-accent)]'
                        : 'text-[var(--text-secondary)] hover:bg-[var(--bg-accent)]'
                    )}
                  >
                    {wf}
                  </button>
                ))}
              </div>
            )}
          </div>
        </div>
        {/* Status badge */}
        <div className="flex items-center gap-2">
          <Activity size={14} style={{ color: statusColor }} />
          <span className="badge text-[10px]" style={{ color: statusColor, borderColor: statusColor }}>
            {statusLabel}
          </span>
        </div>
      </header>
      <div className="divider-h" />

      {/* Main content */}
      <main ref={containerRef} className="flex-1 flex min-h-0 min-w-0 overflow-hidden">
        {/* YAML panel */}
        <div
          className="min-w-0 overflow-hidden shrink-0"
          style={{ width: `${leftPanelWidth}%`, background: 'var(--bg-surface)' }}
        >
          <YamlViewer yaml={workflowYaml} loading={yamlLoading} />
        </div>
        {/* Draggable divider */}
        <div
          className="divider-v shrink-0 relative z-10 group"
          onMouseDown={handleMouseDown}
        >
          <div
            className="absolute inset-y-0 -left-[3px] w-[7px] cursor-col-resize transition-colors"
            style={{ background: 'transparent' }}
            onMouseEnter={(e) => { (e.currentTarget as HTMLElement).style.background = 'rgba(255,255,255,0.06)' }}
            onMouseLeave={(e) => { (e.currentTarget as HTMLElement).style.background = 'transparent' }}
          />
        </div>
        {/* Research stream panel + input */}
        <div className="flex-1 min-w-0 overflow-hidden flex flex-col">
          <ResearchStream
            messages={messages}
            toolCalls={toolCalls}
            timeline={timeline}
            runStatus={runStatus}
            currentStep={currentStep}
            error={error}
            iterationCount={iterationCount}
          />
          <div className="divider-h" />
          <InputBar
            runStatus={runStatus}
            onRun={handleRun}
            onStop={stopRun}
            selectedWorkflow={selectedWorkflow ?? undefined}
          />
        </div>
      </main>
    </div>
  )
}
