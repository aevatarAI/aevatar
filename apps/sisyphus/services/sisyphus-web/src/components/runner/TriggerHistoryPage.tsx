import { useState, useEffect, useCallback } from 'react'
import { Link } from 'react-router-dom'
import { Clock, CheckCircle2, AlertCircle, Loader2, ArrowLeft, XCircle } from 'lucide-react'
import { useAuth } from '../../auth/useAuth'
import { fetchTriggerHistory } from '../../api/runner-api'
import type { TriggerHistoryItem } from '../../types/runner'

const STATUS_CONFIG: Record<string, { color: string; icon: typeof CheckCircle2 }> = {
  running: { color: 'var(--neon-gold)', icon: Loader2 },
  completed: { color: 'var(--neon-green)', icon: CheckCircle2 },
  failed: { color: 'var(--neon-red)', icon: AlertCircle },
  stopped: { color: 'var(--text-dimmed)', icon: XCircle },
}

function formatDuration(ms?: number): string {
  if (!ms) return '-'
  if (ms < 1000) return `${ms}ms`
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`
  return `${Math.floor(ms / 60_000)}m ${Math.round((ms % 60_000) / 1000)}s`
}

function formatTime(ts: string): string {
  try {
    return new Date(ts).toLocaleString('en-US', {
      month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
    })
  } catch {
    return ts
  }
}

export default function TriggerHistoryPage() {
  const { getAccessToken } = useAuth()
  const [history, setHistory] = useState<TriggerHistoryItem[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    const token = getAccessToken()
    if (!token) return
    setLoading(true)
    try {
      const data = await fetchTriggerHistory(token)
      setHistory(data)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load history')
    } finally {
      setLoading(false)
    }
  }, [getAccessToken])

  useEffect(() => {
    load()
  }, [load])

  return (
    <div className="h-full overflow-auto p-6">
      <div className="w-full">
        <div className="flex items-center gap-3 mb-6">
          <Link to="/workflows" className="icon-btn"><ArrowLeft size={16} /></Link>
          <div>
            <h1 className="text-lg font-semibold" style={{ color: 'var(--text-primary)' }}>
              Trigger History
            </h1>
            <p className="text-xs mt-1" style={{ color: 'var(--text-dimmed)' }}>
              Workflow execution history
            </p>
          </div>
        </div>

        {loading && (
          <div className="flex items-center gap-2 py-8 justify-center">
            <Loader2 size={16} className="animate-spin" style={{ color: 'var(--text-dimmed)' }} />
          </div>
        )}

        {error && (
          <div className="px-4 py-3 rounded mb-4" style={{ background: 'rgba(252,165,165,0.08)', border: '1px solid rgba(252,165,165,0.2)' }}>
            <span className="text-xs" style={{ color: 'var(--accent-red)' }}>{error}</span>
          </div>
        )}

        {!loading && history.length === 0 && !error && (
          <div className="text-center py-12">
            <Clock size={32} style={{ color: 'var(--text-dimmed)' }} className="mx-auto mb-3" />
            <p className="text-sm" style={{ color: 'var(--text-dimmed)' }}>No workflow runs yet</p>
          </div>
        )}

        <div className="space-y-2">
          {history.map((item) => {
            const cfg = STATUS_CONFIG[item.status] ?? STATUS_CONFIG.stopped
            const Icon = cfg.icon
            return (
              <Link
                key={item.id}
                to={`/workflows/history/${item.id}`}
                className="card card-hover flex items-center gap-4 px-4 py-3"
              >
                <Icon
                  size={16}
                  style={{ color: cfg.color }}
                  className={item.status === 'running' ? 'animate-spin' : ''}
                />
                <div className="flex-1 min-w-0">
                  <div className="text-sm font-medium" style={{ color: 'var(--text-primary)' }}>
                    {item.workflowName}
                  </div>
                  <div className="text-[11px] mt-0.5 flex items-center gap-2" style={{ color: 'var(--text-dimmed)' }}>
                    <span>{item.triggeredBy}</span>
                    <span>|</span>
                    <span>{formatTime(item.triggeredAt)}</span>
                  </div>
                </div>
                <div className="flex items-center gap-3 shrink-0">
                  <span className="text-[10px] font-mono" style={{ color: 'var(--text-dimmed)' }}>
                    {formatDuration(item.durationMs)}
                  </span>
                  <span
                    className="badge text-[10px]"
                    style={{ color: cfg.color, borderColor: cfg.color }}
                  >
                    {item.status}
                  </span>
                </div>
              </Link>
            )
          })}
        </div>
      </div>
    </div>
  )
}
