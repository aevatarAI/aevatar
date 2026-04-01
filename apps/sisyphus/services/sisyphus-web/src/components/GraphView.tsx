import { useRef, useState, useMemo, useCallback, useEffect } from 'react'
import ForceGraph2D from 'react-force-graph-2d'
import { RefreshCw, Maximize2 } from 'lucide-react'
import { useGraphData } from '../hooks/use-graph-data'
import { getNodeColor } from '../types/graph'
import type { GraphNode, GraphEdge } from '../types/graph'
import NodeDetailsPanel from './NodeDetailsPanel'
import EdgeDetailsPanel from './EdgeDetailsPanel'

function hashStr(s: string): number {
  let h = 0
  for (let i = 0; i < s.length; i++) {
    h = ((h << 5) - h + s.charCodeAt(i)) | 0
  }
  return Math.abs(h)
}

function hexAlpha(hex: string, alpha: number): string {
  return hex + Math.round(alpha * 255).toString(16).padStart(2, '0')
}

interface GraphLink {
  source: string
  target: string
  type: string
  id: string
  properties?: Record<string, unknown>
}

interface GraphViewProps {
  selectedNodeIds?: Set<string>
  onNodeSelect?: (nodeId: string, multi: boolean) => void
  onClearSelection?: () => void
}

export default function GraphView({ selectedNodeIds, onNodeSelect, onClearSelection }: GraphViewProps) {
  const {
    snapshot, selectedNode, traverseResult, loading, error, refresh, selectNode, clearSelection,
    filters, setFilters, filterOptions, loadMore, totalFiltered, totalNodes, totalEdges,
  } = useGraphData()
  const fgRef = useRef<any>(null)
  const containerRef = useRef<HTMLDivElement>(null)
  const [hoveredNode, setHoveredNode] = useState<GraphNode | null>(null)
  const [selectedEdge, setSelectedEdge] = useState<GraphEdge | null>(null)
  const [tooltipPos, setTooltipPos] = useState({ x: 0, y: 0 })
  const [dimensions, setDimensions] = useState({ width: 800, height: 600 })

  // Track container dimensions
  useEffect(() => {
    const container = containerRef.current
    if (!container) return
    const observer = new ResizeObserver((entries) => {
      for (const entry of entries) {
        setDimensions({
          width: entry.contentRect.width,
          height: entry.contentRect.height,
        })
      }
    })
    observer.observe(container)
    return () => observer.disconnect()
  }, [])

  // Adjust force based on graph size
  useEffect(() => {
    const fg = fgRef.current
    if (!fg) return
    const n = snapshot?.nodes.length ?? 0
    if (n > 2000) {
      fg.d3Force('charge')?.strength(-10)
      fg.d3Force('link')?.distance(20)
    } else if (n > 500) {
      fg.d3Force('charge')?.strength(-20)
      fg.d3Force('link')?.distance(35)
    } else {
      fg.d3Force('charge')?.strength(-30)
      fg.d3Force('link')?.distance(45)
    }
  }, [snapshot])

  const graphData = useMemo(() => {
    if (!snapshot) return { nodes: [], links: [] }
    const nodes = snapshot.nodes.map((n) => ({ ...n }))
    const links: GraphLink[] = snapshot.edges.map((e) => ({
      source: e.source,
      target: e.target,
      type: e.type,
      id: e.id,
      properties: e.properties,
    }))
    return { nodes, links }
  }, [snapshot])

  const handleNodeClick = useCallback(
    (node: any, event: MouseEvent) => {
      // Multi-select with Ctrl/Cmd key
      if (onNodeSelect && (event.ctrlKey || event.metaKey)) {
        onNodeSelect(node.id, true)
        return
      }

      // Single select for node details
      if (onNodeSelect) {
        onNodeSelect(node.id, false)
      }

      setSelectedEdge(null)
      selectNode(node.id)
      if (fgRef.current && node.x != null && node.y != null) {
        fgRef.current.centerAt(node.x, node.y, 500)
      }
    },
    [selectNode, onNodeSelect],
  )

  const handleNodeHover = useCallback(
    (node: any) => {
      setHoveredNode(node ? (node as GraphNode) : null)
    },
    [],
  )

  const handleNodeDragEnd = useCallback((node: any) => {
    node.fx = undefined
    node.fy = undefined
  }, [])

  const handlePointerMove = useCallback((e: React.PointerEvent) => {
    setTooltipPos({ x: e.clientX, y: e.clientY })
  }, [])

  const handleFitToView = useCallback(() => {
    if (fgRef.current) {
      fgRef.current.zoomToFit(400, 60)
    }
  }, [])

  const handleLinkClick = useCallback(
    (link: any) => {
      // Find the edge in snapshot
      const edge = snapshot?.edges.find((e) => e.id === link.id)
      if (edge) {
        setSelectedEdge(edge)
        clearSelection()
      }
    },
    [snapshot, clearSelection],
  )

  const handleBackgroundClick = useCallback(() => {
    clearSelection()
    setSelectedEdge(null)
    if (onClearSelection) onClearSelection()
  }, [clearSelection, onClearSelection])

  const isMultiSelected = useCallback(
    (nodeId: string) => selectedNodeIds?.has(nodeId) ?? false,
    [selectedNodeIds],
  )

  const isLargeGraph = (snapshot?.nodes.length ?? 0) > 500

  const nodeCanvasObject = useCallback(
    (node: any, ctx: CanvasRenderingContext2D, globalScale: number) => {
      const color = getNodeColor(node as GraphNode)
      const x = node.x ?? 0
      const y = node.y ?? 0
      const isHovered = hoveredNode?.id === node.id
      const isSelected = selectedNode?.id === node.id
      const isMulti = isMultiSelected(node.id)

      const baseRadius = isHovered ? 12 : isSelected || isMulti ? 11 : 10

      if (!isLargeGraph) {
        // Full quality: glow + pulse
        const t = Date.now() * 0.001
        const phase = hashStr(node.id) * 0.37
        const pulse = 0.8 + 0.2 * Math.sin(t * 1.8 + phase)

        ctx.save()
        ctx.shadowColor = color
        ctx.shadowBlur = (isHovered ? 35 : 20) * pulse
        ctx.beginPath()
        ctx.arc(x, y, baseRadius + 2, 0, Math.PI * 2)
        ctx.fillStyle = hexAlpha(color, 0.1 * pulse)
        ctx.fill()
        ctx.restore()

        ctx.save()
        ctx.shadowColor = color
        ctx.shadowBlur = 10
        ctx.beginPath()
        ctx.arc(x, y, baseRadius, 0, Math.PI * 2)
        ctx.fillStyle = color
        ctx.fill()
        ctx.restore()

        ctx.beginPath()
        ctx.arc(x, y, baseRadius * 0.3, 0, Math.PI * 2)
        ctx.fillStyle = `rgba(255,255,255,${(0.55 * pulse).toFixed(2)})`
        ctx.fill()
      } else {
        // Large graph: simple circle, no shadows
        ctx.beginPath()
        ctx.arc(x, y, baseRadius, 0, Math.PI * 2)
        ctx.fillStyle = color
        ctx.fill()
      }

      if (isSelected) {
        ctx.beginPath()
        ctx.arc(x, y, baseRadius + 5, 0, Math.PI * 2)
        ctx.strokeStyle = 'rgba(255,255,255,0.7)'
        ctx.lineWidth = 1
        ctx.setLineDash([3, 3])
        ctx.stroke()
        ctx.setLineDash([])
      }

      if (isMulti) {
        ctx.beginPath()
        ctx.arc(x, y, baseRadius + 6, 0, Math.PI * 2)
        ctx.strokeStyle = 'var(--neon-cyan)'
        ctx.lineWidth = 1.5
        ctx.stroke()
      }

      // Label: show at lower zoom threshold for large graphs
      const labelThreshold = isLargeGraph ? 1.5 : 0.5
      if (globalScale > labelThreshold) {
        const status = node.properties?.sisyphus_status
        const label = typeof status === 'string' ? status : (node.type || '')
        const fontSize = Math.max(9 / globalScale, 2.5)
        ctx.font = `500 ${fontSize}px Inter, sans-serif`
        ctx.textAlign = 'center'
        ctx.textBaseline = 'top'
        ctx.fillStyle = hexAlpha(color, 0.7)
        ctx.fillText(label, x, y + baseRadius + 4)
      }
    },
    [selectedNode, hoveredNode, isMultiSelected, isLargeGraph],
  )

  const linkCanvasObject = useCallback(
    (link: any, ctx: CanvasRenderingContext2D) => {
      const src = link.source
      const tgt = link.target
      if (src?.x == null || tgt?.x == null) return

      const isEdgeSelected = selectedEdge?.id === link.id

      ctx.beginPath()
      ctx.moveTo(src.x, src.y)
      ctx.lineTo(tgt.x, tgt.y)
      ctx.strokeStyle = isEdgeSelected
        ? 'rgba(125, 211, 252, 0.4)'
        : 'rgba(0, 212, 255, 0.07)'
      ctx.lineWidth = isEdgeSelected ? 1.5 : 0.5
      ctx.stroke()

      // Particle animation only for small graphs
      if (!isLargeGraph) {
        const t = Date.now() * 0.001
        const dur = 3 + (hashStr(link.id ?? `${src.id}-${tgt.id}`) % 4)
        const progress = (t / dur) % 1
        const px = src.x + (tgt.x - src.x) * progress
        const py = src.y + (tgt.y - src.y) * progress

        ctx.save()
        ctx.shadowColor = 'var(--neon-cyan)'
        ctx.shadowBlur = 4
        ctx.beginPath()
        ctx.arc(px, py, 0.8, 0, Math.PI * 2)
        ctx.fillStyle = 'rgba(0, 212, 255, 0.45)'
        ctx.fill()
        ctx.restore()
      }
    },
    [selectedEdge, isLargeGraph],
  )

  // Loading state
  if (!snapshot && loading) {
    return (
      <div className="h-full w-full flex items-center justify-center" style={{ background: 'var(--bg-base)' }}>
        <div className="thinking-indicator">
          <div className="thinking-dots" style={{ '--dot-color': 'var(--neon-cyan)' } as React.CSSProperties}>
            <span />
            <span />
            <span />
          </div>
          <span className="text-xs" style={{ color: 'var(--text-dimmed)' }}>
            Loading graph...
          </span>
        </div>
      </div>
    )
  }

  // Error state
  if (error && !snapshot) {
    return (
      <div className="h-full w-full flex items-center justify-center" style={{ background: 'var(--bg-base)' }}>
        <p className="text-xs" style={{ color: 'var(--accent-red)' }}>
          {error}
        </p>
      </div>
    )
  }

  // Empty state
  if (snapshot && snapshot.nodes.length === 0) {
    return (
      <div className="h-full w-full flex items-center justify-center" style={{ background: 'var(--bg-base)' }}>
        <p className="text-xs" style={{ color: 'var(--text-dimmed)' }}>
          No graph data yet. Start a research run to see the knowledge graph.
        </p>
      </div>
    )
  }

  return (
    <div
      ref={containerRef}
      className="h-full w-full relative overflow-hidden graph-cyber-bg"
      onPointerMove={handlePointerMove}
    >
      {/* Force Graph */}
      {snapshot && (
        <ForceGraph2D
          ref={fgRef}
          width={dimensions.width}
          height={dimensions.height}
          graphData={graphData}
          nodeCanvasObject={nodeCanvasObject}
          nodePointerAreaPaint={(node: any, color: string, ctx: CanvasRenderingContext2D) => {
            ctx.beginPath()
            ctx.arc(node.x ?? 0, node.y ?? 0, 8, 0, Math.PI * 2)
            ctx.fillStyle = color
            ctx.fill()
          }}
          linkCanvasObject={linkCanvasObject}
          linkCanvasObjectMode={() => 'replace'}
          onNodeClick={handleNodeClick}
          onNodeHover={handleNodeHover}
          onNodeDragEnd={handleNodeDragEnd}
          onLinkClick={handleLinkClick}
          onBackgroundClick={handleBackgroundClick}
          backgroundColor="rgba(0,0,0,0)"
          warmupTicks={graphData.nodes.length > 1000 ? 100 : 50}
          cooldownTicks={graphData.nodes.length > 1000 ? 200 : 400}
          d3AlphaDecay={graphData.nodes.length > 1000 ? 0.02 : 0.008}
          d3VelocityDecay={graphData.nodes.length > 1000 ? 0.4 : 0.3}
          enableNodeDrag={true}
        />
      )}

      {/* Tooltip */}
      {hoveredNode && (
        <div
          className="fixed px-3 py-1.5 rounded text-xs pointer-events-none z-50"
          style={{
            left: tooltipPos.x + 12,
            top: tooltipPos.y - 8,
            background: 'rgba(10, 10, 12, 0.9)',
            border: '1px solid rgba(125, 211, 252, 0.2)',
            boxShadow: '0 0 12px rgba(125, 211, 252, 0.08)',
            backdropFilter: 'blur(8px)',
          }}
        >
          <div className="font-medium" style={{ color: getNodeColor(hoveredNode) }}>
            {hoveredNode.properties?.name as string ?? hoveredNode.type}
          </div>
          <div className="text-[10px] font-mono mt-0.5" style={{ color: 'var(--text-dimmed)' }}>
            {hoveredNode.properties?.sisyphus_status as string ?? hoveredNode.type} | {hoveredNode.id.slice(0, 16)}...
          </div>
        </div>
      )}

      {/* Toolbar (top-right) */}
      <div className="absolute top-3 right-3 flex items-center gap-1 z-10">
        <button
          onClick={refresh}
          className="icon-btn"
          title="Refresh graph"
          style={{
            background: 'rgba(10, 10, 12, 0.8)',
            border: '1px solid rgba(125, 211, 252, 0.15)',
          }}
        >
          <RefreshCw size={14} className={loading ? 'animate-spin' : ''} style={{ color: 'var(--neon-cyan)' }} />
        </button>
        <button
          onClick={handleFitToView}
          className="icon-btn"
          title="Fit to view"
          style={{
            background: 'rgba(10, 10, 12, 0.8)',
            border: '1px solid rgba(125, 211, 252, 0.15)',
          }}
        >
          <Maximize2 size={14} style={{ color: 'var(--neon-cyan)' }} />
        </button>
      </div>

      {/* Filter bar (top-left) */}
      <div
        className="absolute top-3 left-3 flex items-center gap-2 z-10"
      >
        <input
          type="text"
          placeholder="Search nodes..."
          value={filters.search}
          onChange={(e) => setFilters((f) => ({ ...f, search: e.target.value }))}
          className="text-[11px] px-2.5 py-1.5 rounded font-mono"
          style={{
            background: 'rgba(10, 10, 12, 0.85)',
            border: '1px solid rgba(125, 211, 252, 0.15)',
            color: '#ccc',
            width: 160,
            outline: 'none',
          }}
        />
        {filterOptions.types.length > 0 && (
          <select
            value={filters.type}
            onChange={(e) => setFilters((f) => ({ ...f, type: e.target.value }))}
            className="text-[11px] px-2 py-1.5 rounded font-mono"
            style={{
              background: 'rgba(10, 10, 12, 0.85)',
              border: '1px solid rgba(125, 211, 252, 0.15)',
              color: '#ccc',
              outline: 'none',
            }}
          >
            <option value="">All types</option>
            {filterOptions.types.map((t) => <option key={t} value={t}>{t}</option>)}
          </select>
        )}
        {filterOptions.statuses.length > 0 && (
          <select
            value={filters.status}
            onChange={(e) => setFilters((f) => ({ ...f, status: e.target.value }))}
            className="text-[11px] px-2 py-1.5 rounded font-mono"
            style={{
              background: 'rgba(10, 10, 12, 0.85)',
              border: '1px solid rgba(125, 211, 252, 0.15)',
              color: '#ccc',
              outline: 'none',
            }}
          >
            <option value="">All statuses</option>
            {filterOptions.statuses.map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
        )}
        {(filters.type || filters.status || filters.search) && (
          <button
            onClick={() => setFilters({ type: '', status: '', search: '' })}
            className="text-[10px] px-2 py-1 rounded"
            style={{
              background: 'rgba(255,68,68,0.15)',
              border: '1px solid rgba(255,68,68,0.3)',
              color: 'var(--neon-red)',
            }}
          >
            Clear
          </button>
        )}
      </div>

      {/* Stats bar + load more (bottom-left) */}
      {snapshot && (
        <div
          className="absolute bottom-3 left-3 flex items-center gap-3 px-2.5 py-1.5 rounded z-10 text-[10px] font-mono"
          style={{
            background: 'rgba(10, 10, 12, 0.8)',
            border: '1px solid rgba(125, 211, 252, 0.12)',
            color: 'var(--neon-cyan)',
            textShadow: '0 0 6px rgba(125, 211, 252, 0.3)',
          }}
        >
          <span>{snapshot.nodes.length} / {totalFiltered} nodes</span>
          <span style={{ color: 'rgba(125, 211, 252, 0.3)' }}>|</span>
          <span>{snapshot.edges.length} edges</span>
          <span style={{ color: 'rgba(125, 211, 252, 0.3)' }}>|</span>
          <span style={{ color: '#666' }}>total: {totalNodes}n / {totalEdges}e</span>
          {snapshot.nodes.length < totalFiltered && (
            <>
              <span style={{ color: 'rgba(125, 211, 252, 0.3)' }}>|</span>
              <button
                onClick={loadMore}
                className="underline"
                style={{ color: 'var(--neon-cyan)' }}
              >
                +200 more
              </button>
            </>
          )}
          {selectedNodeIds && selectedNodeIds.size > 0 && (
            <>
              <span style={{ color: 'rgba(125, 211, 252, 0.3)' }}>|</span>
              <span>{selectedNodeIds.size} selected</span>
            </>
          )}
        </div>
      )}

      {/* Node Details Panel */}
      <NodeDetailsPanel
        node={selectedNode}
        traverseResult={traverseResult}
        onClose={clearSelection}
      />

      {/* Edge Details Panel */}
      {selectedEdge && !selectedNode && (
        <EdgeDetailsPanel
          edge={selectedEdge}
          nodes={snapshot?.nodes ?? []}
          onClose={() => setSelectedEdge(null)}
        />
      )}
    </div>
  )
}
