import { useRef, useState, useMemo, useCallback, useEffect } from 'react'
import ForceGraph2D from 'react-force-graph-2d'
import { RefreshCw, Maximize2 } from 'lucide-react'
import { useGraphData } from '../hooks/use-graph-data'
import { NODE_COLORS } from '../types/graph'
import type { GraphNode } from '../types/graph'
import NodeDetailsPanel from './NodeDetailsPanel'

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
}

export default function GraphView() {
  const { snapshot, selectedNode, traverseResult, loading, error, refresh, selectNode, clearSelection } =
    useGraphData()
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

  // Tighten node spacing: weaker repulsion + shorter links
  useEffect(() => {
    const fg = fgRef.current
    if (!fg) return
    fg.d3Force('charge')?.strength(-8)
    fg.d3Force('link')?.distance(15)
  }, [snapshot])

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
      setHoveredNode(node ? (node as GraphNode) : null)
    },
    [],
  )

  const handleNodeDragEnd = useCallback((node: any) => {
    // Unfix the node so it drifts with force simulation (inertia feel)
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

  const nodeCanvasObject = useCallback(
    (node: any, ctx: CanvasRenderingContext2D, globalScale: number) => {
      const type = node.type || 'Unknown'
      const color = NODE_COLORS[type] ?? NODE_COLORS.Default
      const x = node.x ?? 0
      const y = node.y ?? 0
      const isHovered = hoveredNode?.id === node.id
      const isSelected = selectedNode?.id === node.id

      // Animated pulse per-node (unique phase from id hash)
      const t = Date.now() * 0.001
      const phase = hashStr(node.id) * 0.37
      const pulse = 0.8 + 0.2 * Math.sin(t * 1.8 + phase)

      const baseRadius = isHovered ? 12 : isSelected ? 11 : 10

      // Outer bloom
      ctx.save()
      ctx.shadowColor = color
      ctx.shadowBlur = (isHovered ? 35 : 20) * pulse
      ctx.beginPath()
      ctx.arc(x, y, baseRadius + 2, 0, Math.PI * 2)
      ctx.fillStyle = hexAlpha(color, 0.1 * pulse)
      ctx.fill()
      ctx.restore()

      // Core circle
      ctx.save()
      ctx.shadowColor = color
      ctx.shadowBlur = 10
      ctx.beginPath()
      ctx.arc(x, y, baseRadius, 0, Math.PI * 2)
      ctx.fillStyle = color
      ctx.fill()
      ctx.restore()

      // Hot center spot
      ctx.beginPath()
      ctx.arc(x, y, baseRadius * 0.3, 0, Math.PI * 2)
      ctx.fillStyle = `rgba(255,255,255,${(0.55 * pulse).toFixed(2)})`
      ctx.fill()

      // Selected ring (dashed, white)
      if (isSelected) {
        ctx.beginPath()
        ctx.arc(x, y, baseRadius + 5, 0, Math.PI * 2)
        ctx.strokeStyle = 'rgba(255,255,255,0.7)'
        ctx.lineWidth = 1
        ctx.setLineDash([3, 3])
        ctx.stroke()
        ctx.setLineDash([])
      }

      // Type label (only when zoomed enough)
      if (globalScale > 0.5) {
        const fontSize = Math.max(9 / globalScale, 2.5)
        ctx.font = `500 ${fontSize}px Inter, sans-serif`
        ctx.textAlign = 'center'
        ctx.textBaseline = 'top'
        ctx.fillStyle = hexAlpha(color, 0.7)
        ctx.fillText(type, x, y + baseRadius + 4)
      }
    },
    [selectedNode, hoveredNode],
  )

  const linkCanvasObject = useCallback(
    (link: any, ctx: CanvasRenderingContext2D) => {
      const src = link.source
      const tgt = link.target
      if (src?.x == null || tgt?.x == null) return

      // Subtle neon line
      ctx.beginPath()
      ctx.moveTo(src.x, src.y)
      ctx.lineTo(tgt.x, tgt.y)
      ctx.strokeStyle = 'rgba(0, 212, 255, 0.07)'
      ctx.lineWidth = 0.5
      ctx.stroke()

      // Flowing particle along edge
      const t = Date.now() * 0.001
      const dur = 3 + (hashStr(link.id ?? `${src.id}-${tgt.id}`) % 4)
      const progress = (t / dur) % 1
      const px = src.x + (tgt.x - src.x) * progress
      const py = src.y + (tgt.y - src.y) * progress

      ctx.save()
      ctx.shadowColor = '#00d4ff'
      ctx.shadowBlur = 4
      ctx.beginPath()
      ctx.arc(px, py, 0.8, 0, Math.PI * 2)
      ctx.fillStyle = 'rgba(0, 212, 255, 0.45)'
      ctx.fill()
      ctx.restore()
    },
    [],
  )

  // Loading state
  if (!snapshot && loading) {
    return (
      <div className="flex-1 flex items-center justify-center" style={{ background: 'var(--bg-base)' }}>
        <div className="thinking-indicator">
          <div className="thinking-dots" style={{ '--dot-color': '#00ffff' } as React.CSSProperties}>
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
      className="flex-1 relative overflow-hidden graph-cyber-bg"
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
          backgroundColor="rgba(0,0,0,0)"
          d3AlphaDecay={0.008}
          d3VelocityDecay={0.3}
          cooldownTime={Infinity}
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
            border: '1px solid rgba(0, 255, 255, 0.2)',
            boxShadow: '0 0 12px rgba(0, 255, 255, 0.08)',
            backdropFilter: 'blur(8px)',
          }}
        >
          <div className="font-medium" style={{ color: NODE_COLORS[hoveredNode.type] ?? '#00ffff' }}>
            {hoveredNode.type}
          </div>
          <div className="text-[10px] font-mono mt-0.5" style={{ color: 'var(--text-dimmed)' }}>
            {hoveredNode.id.slice(0, 16)}...
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
            border: '1px solid rgba(0, 255, 255, 0.15)',
          }}
        >
          <RefreshCw size={14} className={loading ? 'animate-spin' : ''} style={{ color: '#00ffff' }} />
        </button>
        <button
          onClick={handleFitToView}
          className="icon-btn"
          title="Fit to view"
          style={{
            background: 'rgba(10, 10, 12, 0.8)',
            border: '1px solid rgba(0, 255, 255, 0.15)',
          }}
        >
          <Maximize2 size={14} style={{ color: '#00ffff' }} />
        </button>
      </div>

      {/* Stats bar (bottom-left) */}
      {snapshot && (
        <div
          className="absolute bottom-3 left-3 flex items-center gap-3 px-2.5 py-1.5 rounded z-10 text-[10px] font-mono"
          style={{
            background: 'rgba(10, 10, 12, 0.8)',
            border: '1px solid rgba(0, 255, 255, 0.12)',
            color: '#00ffff',
            textShadow: '0 0 6px rgba(0, 255, 255, 0.3)',
          }}
        >
          <span>{snapshot.nodes.length} nodes</span>
          <span style={{ color: 'rgba(0, 255, 255, 0.3)' }}>|</span>
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
