import { useState, useCallback } from 'react'
import { Play, Square, Loader2, AlertCircle, CheckCircle2, WifiOff } from 'lucide-react'
import { useAuth } from '../../auth/useAuth'
import { startWorkflowRun, stopWorkflowRun } from '../../api/runner-api'
import { useRunnerWebSocket, type RunnerEvent } from '../../hooks/use-runner-websocket'

const WORKFLOW_TYPES = [
  { name: 'research', label: 'Research', description: 'Iterative knowledge generation and verification' },
  { name: 'translate', label: 'Translate', description: 'Batch translate verified nodes' },
  { name: 'purify', label: 'Purify', description: 'Purify raw (red) nodes to blue' },
  { name: 'verify', label: 'Verify', description: 'Verify blue nodes to black (2/3 quorum)' },
]

function eventColor(type: string): string {
  if (type.includes('Started')) return 'var(--neon-gold)'
  if (type.includes('Completed')) return 'var(--neon-green)'
  if (type.includes('Failed') || type.includes('Error')) return 'var(--neon-red)'
  if (type.includes('Output')) return 'var(--neon-cyan)'
  return 'var(--text-dimmed)'
}

function formatTimestamp(ts?: string): string {
  if (!ts) return ''
  try {
    return new Date(ts).toLocaleTimeString('en-US', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' })
  } catch {
    return ts
  }
}

export default function WorkflowRunPage() {
  const { getAccessToken } = useAuth()
  const { events, status, error: wsError, reconnecting, subscribe, disconnect } = useRunnerWebSocket()

  const [selectedType, setSelectedType] = useState('research')
  const [runId, setRunId] = useState<string | null>(null)
  const [starting, setStarting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleStart = useCallback(async () => {
    const token = getAccessToken()
    if (!token) return

    setStarting(true)
    setError(null)
    try {
      const { runId: id } = await startWorkflowRun({ workflowName: selectedType }, token)
      setRunId(id)
      subscribe(id)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to start workflow')
    } finally {
      setStarting(false)
    }
  }, [selectedType, getAccessToken, subscribe])

  const handleStop = useCallback(async () => {
    if (!runId) return
    const token = getAccessToken()
    if (!token) return

    try {
      await stopWorkflowRun(runId, token)
      disconnect()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to stop workflow')
    }
  }, [runId, getAccessToken, disconnect])

  const isRunning = status === 'running'
  const displayError = error || wsError

  return (
    <div className="h-full overflow-auto p-6">
      <div className="w-full">
        <h1 className="text-lg font-semibold mb-6" style={{ color: 'var(--text-primary)' }}>
          Run Workflow
        </h1>

        {/* Workflow Type Selection */}
        <div className="grid grid-cols-2 gap-3 mb-6">
          {WORKFLOW_TYPES.map((wt) => (
            <button
              key={wt.name}
              onClick={() => !isRunning && setSelectedType(wt.name)}
              className="card text-left px-4 py-3 transition-all"
              style={{
                borderColor: selectedType === wt.name ? 'var(--neon-cyan)' : 'var(--border-subtle)',
                background: selectedType === wt.name ? 'rgba(125,211,252,0.05)' : 'var(--bg-surface)',
                cursor: isRunning ? 'not-allowed' : 'pointer',
                opacity: isRunning && selectedType !== wt.name ? 0.4 : 1,
              }}
            >
              <div className="text-sm font-medium" style={{ color: selectedType === wt.name ? 'var(--neon-cyan)' : 'var(--text-primary)' }}>
                {wt.label}
              </div>
              <div className="text-[11px] mt-0.5" style={{ color: 'var(--text-dimmed)' }}>
                {wt.description}
              </div>
            </button>
          ))}
        </div>

        {/* Controls */}
        <div className="flex items-center gap-3 mb-6">
          {isRunning ? (
            <button onClick={handleStop} className="btn-neon-danger text-xs gap-1.5 py-1.5 px-4">
              <Square size={14} />
              Stop
            </button>
          ) : (
            <button
              onClick={handleStart}
              disabled={starting}
              className="btn-neon-green text-xs gap-1.5 py-1.5 px-4 disabled:opacity-50"
            >
              {starting ? <Loader2 size={14} className="animate-spin" /> : <Play size={14} />}
              Start
            </button>
          )}

          {status !== 'idle' && (
            <span
              className="badge text-[10px]"
              style={{
                color: isRunning ? 'var(--neon-gold)' : status === 'completed' ? 'var(--neon-green)' : 'var(--neon-red)',
                borderColor: isRunning ? 'var(--neon-gold)' : status === 'completed' ? 'var(--neon-green)' : 'var(--neon-red)',
              }}
            >
              {status.toUpperCase()}
            </span>
          )}

          {runId && (
            <span className="text-[10px] font-mono" style={{ color: 'var(--text-dimmed)' }}>
              Run: {runId.slice(0, 12)}...
            </span>
          )}
        </div>

        {/* Error */}
        {displayError && (
          <div className="flex items-center gap-2 px-4 py-3 rounded mb-4" style={{ background: 'rgba(252,165,165,0.08)', border: '1px solid rgba(252,165,165,0.2)' }}>
            <AlertCircle size={14} style={{ color: 'var(--accent-red)' }} />
            <span className="text-xs" style={{ color: 'var(--accent-red)' }}>{displayError}</span>
          </div>
        )}

        {/* Reconnecting indicator */}
        {reconnecting && (
          <div className="flex items-center gap-2 px-4 py-3 rounded mb-4" style={{ background: 'rgba(252,211,77,0.08)', border: '1px solid rgba(252,211,77,0.2)' }}>
            <WifiOff size={14} style={{ color: 'var(--neon-gold)' }} />
            <span className="text-xs" style={{ color: 'var(--neon-gold)' }}>Connection lost. Reconnecting...</span>
            <Loader2 size={12} className="animate-spin ml-auto" style={{ color: 'var(--neon-gold)' }} />
          </div>
        )}

        {/* Event Stream */}
        {events.length > 0 && (
          <div className="card p-4">
            <h2 className="text-[10px] font-semibold uppercase tracking-wider mb-3" style={{ color: 'var(--text-dimmed)' }}>
              Event Stream
            </h2>
            <div className="space-y-1 max-h-[50vh] overflow-auto">
              {events.map((event: RunnerEvent, i: number) => (
                <div
                  key={i}
                  className="flex items-start gap-2 px-2 py-1.5 rounded"
                  style={{ background: 'var(--bg-elevated)' }}
                >
                  {event.type.includes('Completed') ? (
                    <CheckCircle2 size={12} className="shrink-0 mt-0.5" style={{ color: eventColor(event.type) }} />
                  ) : event.type.includes('Failed') ? (
                    <AlertCircle size={12} className="shrink-0 mt-0.5" style={{ color: eventColor(event.type) }} />
                  ) : (
                    <div className="w-3 h-3 rounded-full shrink-0 mt-0.5" style={{ background: eventColor(event.type) }} />
                  )}
                  <div className="flex-1 min-w-0">
                    <span className="text-[11px] font-mono" style={{ color: 'var(--text-secondary)' }}>
                      {event.type}
                    </span>
                    {event.step_name && (
                      <span className="text-[10px] font-mono ml-2" style={{ color: 'var(--text-dimmed)' }}>
                        [{event.step_name}]
                      </span>
                    )}
                    {event.error && (
                      <div className="text-[10px] mt-0.5" style={{ color: 'var(--accent-red)' }}>
                        {event.error}
                      </div>
                    )}
                  </div>
                  <span className="text-[10px] font-mono shrink-0" style={{ color: 'var(--text-dimmed)' }}>
                    {formatTimestamp(event.timestamp)}
                  </span>
                </div>
              ))}
              {isRunning && (
                <div className="flex items-center gap-2 px-2 py-1">
                  <div className="thinking-dots" style={{ '--dot-color': 'var(--accent-gold)' } as React.CSSProperties}>
                    <span />
                    <span />
                    <span />
                  </div>
                </div>
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
