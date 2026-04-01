import { useState, useCallback } from 'react'
import { useLocation } from 'react-router-dom'
import { Loader2, X, CheckSquare, Layers } from 'lucide-react'
import { useAuth } from '../auth/useAuth'
import { exportPaper } from '../api'
import GraphView from './GraphView'

export default function GraphPage() {
  const location = useLocation()
  const isActive = location.pathname === '/graph' || location.pathname === '/'
  const [exporting, setExporting] = useState(false)
  const [exportError, setExportError] = useState<string | null>(null)
  const [selectedNodeIds, setSelectedNodeIds] = useState<Set<string>>(new Set())
  const { getAccessToken } = useAuth()


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

  const handleCompileSelected = useCallback(async () => {
    if (selectedNodeIds.size === 0) return
    setExporting(true)
    setExportError(null)
    try {
      // For selected compilation, we pass node IDs as query params
      const token = getAccessToken()
      if (!token) throw new Error('Not authenticated')

      const ids = Array.from(selectedNodeIds)
      const res = await fetch(`/api/v2/paper?nodeIds=${ids.join(',')}`, {
        headers: { Authorization: `Bearer ${token}` },
      })
      if (!res.ok) throw new Error(`Compile failed: ${res.status}`)
      const blob = await res.blob()
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = 'paper-selection.pdf'
      a.click()
      URL.revokeObjectURL(url)
    } catch (err) {
      setExportError(err instanceof Error ? err.message : 'Compile selected failed')
    } finally {
      setExporting(false)
    }
  }, [selectedNodeIds, getAccessToken])

  const handleNodeSelect = useCallback((nodeId: string, multi: boolean) => {
    setSelectedNodeIds((prev) => {
      const next = new Set(multi ? prev : [])
      if (next.has(nodeId)) {
        next.delete(nodeId)
      } else {
        next.add(nodeId)
      }
      return next
    })
  }, [])

  const handleClearSelection = useCallback(() => {
    setSelectedNodeIds(new Set())
  }, [])

  return (
    <div className="h-full w-full overflow-hidden relative">
      {/* Graph (full screen, behind everything) */}
      <div className="absolute inset-0">
        <GraphView
          selectedNodeIds={selectedNodeIds}
          onNodeSelect={handleNodeSelect}
          onClearSelection={handleClearSelection}
        />
      </div>

      {/* Floating action bar (top center) — only visible on graph page */}
      {isActive && <div className="absolute top-3 left-1/2 -translate-x-1/2 z-20 flex items-center gap-2">
        <button
          onClick={handleExportPdf}
          disabled={exporting}
          className="btn-neon-blue text-xs gap-1.5 py-1.5 px-3 disabled:opacity-30 disabled:cursor-not-allowed"
        >
          <Layers size={14} />
          Compile All
        </button>
        <button
          onClick={handleCompileSelected}
          disabled={exporting || selectedNodeIds.size === 0}
          className="btn-neon-blue text-xs gap-1.5 py-1.5 px-3 disabled:opacity-30 disabled:cursor-not-allowed"
        >
          <CheckSquare size={14} />
          Compile Selected{selectedNodeIds.size > 0 ? ` (${selectedNodeIds.size})` : ''}
        </button>
      </div>}

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
                <span className="text-sm" style={{ color: 'var(--neon-red)', textShadow: '0 0 8px rgba(255,68,68,0.3)' }}>
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
