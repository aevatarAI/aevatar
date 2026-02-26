import { useState } from 'react'
import { Play, Square } from 'lucide-react'
import type { RunStatus } from '../types'

interface InputBarProps {
  runStatus: RunStatus
  onRun: (topic: string, maxIterations: number) => void
  onStop: () => void
  selectedWorkflow?: string
}

export default function InputBar({ runStatus, onRun, onStop }: InputBarProps) {
  const [topic, setTopic] = useState('')
  const [maxIterations, setMaxIterations] = useState(20)

  const isRunning = runStatus === 'running'
  const canRun = topic.trim().length > 0 && !isRunning

  const handleRun = () => {
    if (!canRun) return
    onRun(topic, maxIterations)
  }

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey && canRun) {
      e.preventDefault()
      handleRun()
    }
  }

  return (
    <div className="px-4 py-3" style={{ background: 'var(--bg-surface)' }}>
      <div className="flex gap-3 items-center">
        <textarea
          value={topic}
          onChange={(e) => setTopic(e.target.value)}
          onKeyDown={handleKeyDown}
          placeholder="Enter research topic..."
          disabled={isRunning}
          rows={1}
          className="input resize-none flex-1"
        />
        <div className="flex items-center gap-1.5 shrink-0">
          <label className="text-[11px] font-mono" style={{ color: 'var(--text-dimmed)' }}>iters</label>
          <input
            type="number"
            value={maxIterations}
            onChange={(e) => setMaxIterations(parseInt(e.target.value) || 20)}
            min={1}
            max={100}
            disabled={isRunning}
            className="input py-1.5 px-2 text-xs w-16"
          />
        </div>
        {isRunning ? (
          <button onClick={onStop} className="btn-warning shrink-0">
            <Square size={16} />
            Stop
          </button>
        ) : (
          <button onClick={handleRun} disabled={!canRun} className="btn-primary shrink-0 disabled:opacity-40 disabled:cursor-not-allowed">
            <Play size={16} />
            Run
          </button>
        )}
      </div>
    </div>
  )
}
