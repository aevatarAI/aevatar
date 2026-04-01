import { useState, useEffect, useCallback } from 'react'
import { Link } from 'react-router-dom'
import { ArrowLeft, Loader2, Upload, ChevronDown, ChevronRight } from 'lucide-react'
import { useAuth } from '../../auth/useAuth'
import { fetchUploadHistory, fetchUploadDetail } from '../../api/ingestor-api'
import type { UploadHistoryItem, UploadDetail } from '../../types/runner'

function formatTime(ts: string): string {
  try {
    return new Date(ts).toLocaleString('en-US', {
      month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
    })
  } catch {
    return ts
  }
}

export default function UploadHistoryPage() {
  const { getAccessToken } = useAuth()
  const [history, setHistory] = useState<UploadHistoryItem[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [expandedId, setExpandedId] = useState<string | null>(null)
  const [detail, setDetail] = useState<UploadDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)

  const load = useCallback(async () => {
    const token = getAccessToken()
    if (!token) return
    setLoading(true)
    try {
      const data = await fetchUploadHistory(token)
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

  const toggleExpand = useCallback(
    async (id: string) => {
      if (expandedId === id) {
        setExpandedId(null)
        setDetail(null)
        return
      }

      const token = getAccessToken()
      if (!token) return

      setExpandedId(id)
      setDetailLoading(true)
      try {
        const data = await fetchUploadDetail(id, token)
        setDetail(data)
      } catch {
        setDetail(null)
      } finally {
        setDetailLoading(false)
      }
    },
    [expandedId, getAccessToken],
  )

  return (
    <div className="h-full overflow-auto p-6">
      <div className="w-full">
        <div className="flex items-center gap-3 mb-6">
          <Link to="/upload" className="icon-btn"><ArrowLeft size={16} /></Link>
          <div>
            <h1 className="text-lg font-semibold" style={{ color: 'var(--text-primary)' }}>
              Upload History
            </h1>
            <p className="text-xs mt-1" style={{ color: 'var(--text-dimmed)' }}>
              Knowledge ingestion records
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
            <Upload size={32} style={{ color: 'var(--text-dimmed)' }} className="mx-auto mb-3" />
            <p className="text-sm" style={{ color: 'var(--text-dimmed)' }}>No uploads yet</p>
          </div>
        )}

        <div className="space-y-2">
          {history.map((item) => (
            <div key={item.id}>
              <button
                onClick={() => toggleExpand(item.id)}
                className="card card-hover w-full flex items-center gap-4 px-4 py-3 text-left"
              >
                {expandedId === item.id ? (
                  <ChevronDown size={14} style={{ color: 'var(--text-dimmed)' }} />
                ) : (
                  <ChevronRight size={14} style={{ color: 'var(--text-dimmed)' }} />
                )}
                <div className="flex-1 min-w-0">
                  <div className="text-sm font-medium" style={{ color: 'var(--text-primary)' }}>
                    {item.uploadedBy}
                  </div>
                  <div className="text-[11px] mt-0.5" style={{ color: 'var(--text-dimmed)' }}>
                    {formatTime(item.uploadedAt)}
                  </div>
                </div>
                <div className="flex items-center gap-3 shrink-0">
                  <span className="badge text-[10px] badge-blue">{item.nodesWritten} nodes</span>
                  <span className="badge text-[10px] badge-purple">{item.edgesWritten} edges</span>
                </div>
              </button>

              {/* Expanded detail */}
              {expandedId === item.id && (
                <div className="ml-8 mt-1 mb-2 card px-4 py-3">
                  {detailLoading ? (
                    <Loader2 size={14} className="animate-spin" style={{ color: 'var(--text-dimmed)' }} />
                  ) : detail ? (
                    <div className="space-y-2">
                      {detail.nodeIds.length > 0 && (
                        <div>
                          <h4 className="text-[10px] font-semibold uppercase mb-1" style={{ color: 'var(--text-dimmed)' }}>
                            Node IDs
                          </h4>
                          <div className="text-[10px] font-mono break-all" style={{ color: 'var(--text-secondary)' }}>
                            {detail.nodeIds.join(', ')}
                          </div>
                        </div>
                      )}
                      {detail.edgeIds.length > 0 && (
                        <div>
                          <h4 className="text-[10px] font-semibold uppercase mb-1" style={{ color: 'var(--text-dimmed)' }}>
                            Edge IDs
                          </h4>
                          <div className="text-[10px] font-mono break-all" style={{ color: 'var(--text-secondary)' }}>
                            {detail.edgeIds.join(', ')}
                          </div>
                        </div>
                      )}
                    </div>
                  ) : (
                    <span className="text-xs" style={{ color: 'var(--text-dimmed)' }}>No details available</span>
                  )}
                </div>
              )}
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
