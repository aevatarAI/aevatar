import { Play, Square } from 'lucide-react'
import type { RunStatus } from '../types'

interface InputBarProps {
  runStatus: RunStatus
  onStart: () => void
  onStop: () => void
}

export default function InputBar({ runStatus, onStart, onStop }: InputBarProps) {
  const isRunning = runStatus === 'running'

  return (
    <div className="px-4 py-3" style={{ background: 'var(--bg-surface)' }}>
      <div className="flex gap-3 items-center justify-between">
        <span className="text-[11px] font-mono" style={{ color: 'var(--text-dimmed)' }}>
          {isRunning
            ? 'Research loop is running — new blue nodes are being generated each round'
            : runStatus === 'completed'
              ? 'Research loop finished'
              : 'Start the loop to generate blue nodes from the knowledge graph'}
        </span>
        {isRunning ? (
          <button onClick={onStop} className="btn-warning shrink-0">
            <Square size={16} />
            Stop
          </button>
        ) : (
          <button onClick={onStart} className="btn-primary shrink-0">
            <Play size={16} />
            Start
          </button>
        )}
      </div>
    </div>
  )
}
