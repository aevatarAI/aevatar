import { useState, useEffect, useCallback } from 'react'
import { useParams, Link } from 'react-router-dom'
import { ArrowLeft, Loader2, CheckCircle2, AlertCircle, Clock } from 'lucide-react'
import { useAuth } from '../../auth/useAuth'
import { fetchRunDetail } from '../../api/runner-api'
import type { RunDetail, RunDetailEvent } from '../../types/runner'

function eventColor(type: string): string {
  if (type.includes('Started')) return 'var(--neon-gold)'
  if (type.includes('Completed')) return 'var(--neon-green)'
  if (type.includes('Failed') || type.includes('Error')) return 'var(--neon-red)'
  if (type.includes('Output')) return 'var(--neon-cyan)'
  return 'var(--text-dimmed)'
}

function formatTimestamp(ts: string): string {
  try {
    return new Date(ts).toISOString().slice(11, 23)
  } catch {
    return ts
  }
}

export default function RunDetailView() {
  const { id } = useParams<{ id: string }>()
  const { getAccessToken } = useAuth()
  const [detail, setDetail] = useState<RunDetail | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    if (!id) return
    const token = getAccessToken()
    if (!token) return
    setLoading(true)
    try {
      const data = await fetchRunDetail(id, token)
      setDetail(data)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load run detail')
    } finally {
      setLoading(false)
    }
  }, [id, getAccessToken])

  useEffect(() => {
    load()
  }, [load])

  if (loading) {
    return (
      <div className="h-full flex items-center justify-center">
        <Loader2 size={20} className="animate-spin" style={{ color: 'var(--text-dimmed)' }} />
      </div>
    )
  }

  if (error || !detail) {
    return (
      <div className="h-full flex items-center justify-center">
        <div className="text-center">
          <AlertCircle size={24} style={{ color: 'var(--accent-red)' }} className="mx-auto mb-2" />
          <p className="text-xs" style={{ color: 'var(--accent-red)' }}>{error ?? 'Not found'}</p>
          <Link to="/workflows/history" className="btn-secondary text-xs py-1 px-3 mt-3 inline-flex">Back</Link>
        </div>
      </div>
    )
  }

  return (
    <div className="h-full overflow-auto p-6">
      <div className="w-full">
        {/* Header */}
        <div className="flex items-center gap-3 mb-6">
          <Link to="/workflows/history" className="icon-btn"><ArrowLeft size={16} /></Link>
          <div className="flex-1">
            <h1 className="text-lg font-semibold" style={{ color: 'var(--text-primary)' }}>
              {detail.workflowName}
            </h1>
            <div className="text-[11px] flex items-center gap-2 mt-0.5" style={{ color: 'var(--text-dimmed)' }}>
              <span>{detail.triggeredBy}</span>
              <span>|</span>
              <Clock size={10} />
              <span>{new Date(detail.triggeredAt).toLocaleString()}</span>
              {detail.durationMs && (
                <>
                  <span>|</span>
                  <span>{(detail.durationMs / 1000).toFixed(1)}s</span>
                </>
              )}
            </div>
          </div>
          <span
            className="badge text-[10px]"
            style={{
              color: detail.status === 'completed' ? 'var(--neon-green)' : detail.status === 'failed' ? 'var(--neon-red)' : 'var(--neon-gold)',
              borderColor: detail.status === 'completed' ? 'var(--neon-green)' : detail.status === 'failed' ? 'var(--neon-red)' : 'var(--neon-gold)',
            }}
          >
            {detail.status}
          </span>
        </div>

        {/* Error */}
        {detail.error && (
          <div className="flex items-center gap-2 px-4 py-3 rounded mb-4" style={{ background: 'rgba(252,165,165,0.08)', border: '1px solid rgba(252,165,165,0.2)' }}>
            <AlertCircle size={14} style={{ color: 'var(--accent-red)' }} />
            <span className="text-xs" style={{ color: 'var(--accent-red)' }}>{detail.error}</span>
          </div>
        )}

        {/* Event Log */}
        <div className="card p-4">
          <h2 className="text-[10px] font-semibold uppercase tracking-wider mb-3" style={{ color: 'var(--text-dimmed)' }}>
            Event Log ({detail.events.length} events)
          </h2>
          <div className="space-y-1">
            {detail.events.map((event: RunDetailEvent, i: number) => (
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
                  {event.data && Object.keys(event.data).length > 0 && (
                    <pre className="text-[9px] font-mono mt-0.5 whitespace-pre-wrap" style={{ color: 'var(--text-dimmed)' }}>
                      {JSON.stringify(event.data, null, 2)}
                    </pre>
                  )}
                </div>
                <span className="text-[10px] font-mono shrink-0" style={{ color: 'var(--text-dimmed)' }}>
                  {formatTimestamp(event.timestamp)}
                </span>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  )
}
