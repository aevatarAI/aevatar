import { useState, useCallback, useRef } from 'react'
import { useLocation } from 'react-router-dom'
import { Loader2, X, Layers } from 'lucide-react'
import { useAuth } from '../auth/useAuth'
import { proxyUrl } from '../hooks/use-api'
import GraphView from './GraphView'
import type { GraphSnapshot } from '../types/graph'

export default function GraphPage() {
  const location = useLocation()
  const isActive = location.pathname === '/graph' || location.pathname === '/'
  const [exporting, setExporting] = useState(false)
  const [exportError, setExportError] = useState<string | null>(null)
  const { getAccessToken } = useAuth()
  const filteredSnapshotRef = useRef<GraphSnapshot | null>(null)

  const handleFilteredSnapshotChange = useCallback((snapshot: GraphSnapshot | null) => {
    filteredSnapshotRef.current = snapshot
  }, [])

  const handleCompileCurrentFilter = useCallback(async () => {
    const snap = filteredSnapshotRef.current
    if (!snap || snap.nodes.length === 0) {
      setExportError('No nodes in current filter — adjust filters first')
      return
    }
    const token = getAccessToken()
    if (!token) return

    setExporting(true)
    setExportError(null)
    try {
      const url = proxyUrl('sisyphus-paper-compiler', '/compile')
      const res = await fetch(url, {
        method: 'POST',
        headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({
          nodes: snap.nodes.map((n) => ({ id: n.id, type: n.type, properties: n.properties ?? {} })),
          edges: snap.edges.map((e) => ({ source: e.source, target: e.target, edge_type: e.type ?? 'references' })),
        }),
      })
      if (!res.ok) {
        const body = await res.text().catch(() => '')
        throw new Error(`Compile failed: ${res.status} ${body}`)
      }
      const blob = await res.blob()
      const blobUrl = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = blobUrl
      a.download = 'paper.pdf'
      a.click()
      URL.revokeObjectURL(blobUrl)
    } catch (err) {
      setExportError(err instanceof Error ? err.message : 'PDF export failed')
    } finally {
      setExporting(false)
    }
  }, [getAccessToken])

  return (
    <div className="h-full w-full overflow-hidden relative">
      {/* Graph (full screen, behind everything) */}
      <div className="absolute inset-0">
        <GraphView
          onFilteredSnapshotChange={handleFilteredSnapshotChange}
        />
      </div>

      {/* Floating action bar (top center) — only visible on graph page */}
      {isActive && <div className="absolute top-3 left-1/2 -translate-x-1/2 z-20 flex items-center gap-2">
        <button
          onClick={handleCompileCurrentFilter}
          disabled={exporting}
          className="btn-neon-blue text-xs gap-1.5 py-1.5 px-3 disabled:opacity-30 disabled:cursor-not-allowed"
        >
          <Layers size={14} />
          Compile Current Filter
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
