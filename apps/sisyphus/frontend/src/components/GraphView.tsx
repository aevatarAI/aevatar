import { useRef, useState, useMemo, useCallback, useEffect } from 'react'
import ForceGraph2D from 'react-force-graph-2d'
import { RefreshCw, Maximize2 } from 'lucide-react'
import { useGraphData } from '../hooks/use-graph-data'
import { NODE_COLORS } from '../types/graph'
import type { GraphNode } from '../types/graph'
import type { RunStatus } from '../types'
import NodeDetailsPanel from './NodeDetailsPanel'

interface GraphViewProps {
  runStatus: RunStatus
}

interface GraphLink {
  source: string
  target: string
  type: string
  id: string
}

export default function GraphView({ runStatus }: GraphViewProps) {
  const { snapshot, selectedNode, traverseResult, loading, error, refresh, selectNode, clearSelection } =
    useGraphData(runStatus)
  const fgRef = useRef<any>(null)
  const containerRef = useRef<HTMLDivElement>(null)
  const [hoveredNode, setHoveredNode] = useState<GraphNode | null>(null)
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

  const graphData = useMemo(() => {
    if (!snapshot) return { nodes: [], links: [] }
    const nodes = snapshot.nodes.map((n) => ({ ...n }))
    const links: GraphLink[] = snapshot.edges.map((e) => ({
      source: e.source,
      target: e.target,
      type: e.type,
      id: e.id,
    }))
    return { nodes, links }
  }, [snapshot])

  const handleNodeClick = useCallback(
    (node: any) => {
      selectNode(node.id)
      if (fgRef.current && node.x != null && node.y != null) {
        fgRef.current.centerAt(node.x, node.y, 500)
      }
    },
    [selectNode],
  )

  const handleNodeHover = useCallback(
    (node: any) => {
      if (node) {
        setHoveredNode(node as GraphNode)
      } else {
        setHoveredNode(null)
      }
    },
    [],
  )

  const handlePointerMove = useCallback((e: React.PointerEvent) => {
    setTooltipPos({ x: e.clientX, y: e.clientY })
  }, [])

  const handleFitToView = useCallback(() => {
    if (fgRef.current) {
      fgRef.current.zoomToFit(400, 60)
    }
  }, [])

  const nodeCanvasObject = useCallback(
    (node: any, ctx: CanvasRenderingContext2D, globalScale: number) => {
      const label = node.label || node.id
      const color = NODE_COLORS[node.type] ?? NODE_COLORS.Default
      const radius = 6
      const x = node.x ?? 0
      const y = node.y ?? 0

      // Glow
      ctx.shadowColor = color
      ctx.shadowBlur = 12
      ctx.beginPath()
      ctx.arc(x, y, radius, 0, 2 * Math.PI)
      ctx.fillStyle = color
      ctx.fill()
      ctx.shadowBlur = 0

      // Selected ring
      if (selectedNode && selectedNode.id === node.id) {
        ctx.beginPath()
        ctx.arc(x, y, radius + 3, 0, 2 * Math.PI)
        ctx.strokeStyle = '#ffffff'
        ctx.lineWidth = 1.5
        ctx.stroke()
      }

      // Label
      const fontSize = Math.max(10 / globalScale, 3)
      ctx.font = `${fontSize}px Inter, sans-serif`
      ctx.textAlign = 'center'
      ctx.textBaseline = 'top'
      ctx.fillStyle = 'rgba(250, 250, 250, 0.8)'
      ctx.fillText(label, x, y + radius + 3)
    },
    [selectedNode],
  )

  // Loading state
  if (!snapshot && loading) {
    return (
      <div className="flex-1 flex items-center justify-center" style={{ background: 'var(--bg-base)' }}>
        <div className="thinking-indicator">
          <div className="thinking-dots">
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
      <div className="flex-1 flex items-center justify-center" style={{ background: 'var(--bg-base)' }}>
        <p className="text-xs" style={{ color: 'var(--accent-red)' }}>
          {error}
        </p>
      </div>
    )
  }

  // Empty state
  if (snapshot && snapshot.nodes.length === 0) {
    return (
      <div className="flex-1 flex items-center justify-center" style={{ background: 'var(--bg-base)' }}>
        <p className="text-xs" style={{ color: 'var(--text-dimmed)' }}>
          No graph data yet. Start a research run to see the knowledge graph.
        </p>
      </div>
    )
  }

  return (
    <div
      ref={containerRef}
      className="flex-1 relative graph-grid-bg overflow-hidden"
      style={{ background: 'var(--bg-base)' }}
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
            ctx.arc(node.x ?? 0, node.y ?? 0, 8, 0, 2 * Math.PI)
            ctx.fillStyle = color
            ctx.fill()
          }}
          onNodeClick={handleNodeClick}
          onNodeHover={handleNodeHover}
          linkColor={() => 'rgba(0,255,255,0.4)'}
          linkDirectionalArrowLength={6}
          linkDirectionalArrowRelPos={1}
          backgroundColor="rgba(0,0,0,0)"
          cooldownTicks={100}
        />
      )}

      {/* Tooltip */}
      {hoveredNode && (
        <div
          className="fixed px-2 py-1 rounded text-xs pointer-events-none z-50"
          style={{
            left: tooltipPos.x + 12,
            top: tooltipPos.y - 8,
            background: 'var(--bg-elevated)',
            border: '1px solid var(--border-default)',
            color: 'var(--text-primary)',
          }}
        >
          <div className="font-medium">{hoveredNode.label}</div>
          <div style={{ color: 'var(--text-dimmed)' }}>{hoveredNode.type}</div>
        </div>
      )}

      {/* Toolbar (top-right) */}
      <div className="absolute top-3 right-3 flex items-center gap-1 z-10">
        <button
          onClick={refresh}
          className="icon-btn"
          title="Refresh graph"
          style={{ background: 'var(--bg-surface)', border: '1px solid var(--border-subtle)' }}
        >
          <RefreshCw size={14} className={loading ? 'animate-spin' : ''} />
        </button>
        <button
          onClick={handleFitToView}
          className="icon-btn"
          title="Fit to view"
          style={{ background: 'var(--bg-surface)', border: '1px solid var(--border-subtle)' }}
        >
          <Maximize2 size={14} />
        </button>
      </div>

      {/* Stats bar (bottom-left) */}
      {snapshot && (
        <div
          className="absolute bottom-3 left-3 flex items-center gap-3 px-2.5 py-1.5 rounded z-10 text-[10px] font-mono"
          style={{
            background: 'var(--bg-surface)',
            border: '1px solid var(--border-subtle)',
            color: 'var(--text-dimmed)',
          }}
        >
          <span>{snapshot.nodes.length} nodes</span>
          <span>{snapshot.edges.length} edges</span>
        </div>
      )}

      {/* Node Details Panel */}
      <NodeDetailsPanel
        node={selectedNode}
        traverseResult={traverseResult}
        onClose={clearSelection}
      />
    </div>
  )
}
