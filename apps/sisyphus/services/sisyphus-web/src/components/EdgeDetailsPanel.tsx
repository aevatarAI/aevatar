import { X } from 'lucide-react'
import type { GraphEdge, GraphNode } from '../types/graph'

interface EdgeDetailsPanelProps {
  edge: GraphEdge
  nodes: GraphNode[]
  onClose: () => void
}

const EDGE_TYPE_COLORS: Record<string, string> = {
  proves: 'var(--neon-green)',
  references: 'var(--neon-cyan)',
  translates: '#bf7fff',
}

export default function EdgeDetailsPanel({ edge, nodes, onClose }: EdgeDetailsPanelProps) {
  const sourceNode = nodes.find((n) => n.id === edge.source)
  const targetNode = nodes.find((n) => n.id === edge.target)

  const typeColor = EDGE_TYPE_COLORS[edge.type] ?? 'var(--text-muted)'

  return (
    <div
      className="absolute right-0 top-0 bottom-0 flex flex-col overflow-hidden z-20 animate-slide-in"
      style={{
        width: 'min(420px, 40vw)',
        background: 'rgba(10, 10, 12, 0.3)',
        backdropFilter: 'blur(20px) saturate(1.3)',
        WebkitBackdropFilter: 'blur(20px) saturate(1.3)',
        borderLeft: '1px solid rgba(255, 255, 255, 0.06)',
        boxShadow: '-4px 0 24px rgba(0, 0, 0, 0.15)',
      }}
    >
      {/* Header */}
      <div
        className="flex items-center justify-between px-4 py-3 shrink-0"
        style={{ borderBottom: '1px solid var(--border-default)' }}
      >
        <div className="flex items-center gap-2">
          <span
            className="text-xs font-mono px-2 py-0.5 rounded"
            style={{ color: typeColor, background: 'var(--bg-accent)' }}
          >
            {edge.type}
          </span>
          <span className="text-sm font-semibold" style={{ color: 'var(--text-primary)' }}>
            Edge
          </span>
        </div>
        <button onClick={onClose} className="icon-btn">
          <X size={16} />
        </button>
      </div>

      {/* Body */}
      <div className="flex-1 overflow-y-auto p-4 space-y-5">
        {/* Source */}
        <section>
          <h3
            className="text-[10px] font-semibold uppercase tracking-wider mb-2"
            style={{ color: 'var(--text-dimmed)' }}
          >
            Source
          </h3>
          <div
            className="px-3 py-2 rounded"
            style={{ background: 'var(--bg-elevated)' }}
          >
            <div className="text-xs font-medium" style={{ color: 'var(--text-secondary)' }}>
              {sourceNode?.label ?? edge.source}
            </div>
            <div className="text-[10px] font-mono mt-0.5" style={{ color: 'var(--text-dimmed)' }}>
              {edge.source}
            </div>
          </div>
        </section>

        {/* Arrow */}
        <div className="flex justify-center">
          <div className="text-xs" style={{ color: typeColor }}>
            ---{edge.type}---&gt;
          </div>
        </div>

        {/* Target */}
        <section>
          <h3
            className="text-[10px] font-semibold uppercase tracking-wider mb-2"
            style={{ color: 'var(--text-dimmed)' }}
          >
            Target
          </h3>
          <div
            className="px-3 py-2 rounded"
            style={{ background: 'var(--bg-elevated)' }}
          >
            <div className="text-xs font-medium" style={{ color: 'var(--text-secondary)' }}>
              {targetNode?.label ?? edge.target}
            </div>
            <div className="text-[10px] font-mono mt-0.5" style={{ color: 'var(--text-dimmed)' }}>
              {edge.target}
            </div>
          </div>
        </section>

        {/* Properties */}
        {edge.properties && Object.keys(edge.properties).length > 0 && (
          <section>
            <h3
              className="text-[10px] font-semibold uppercase tracking-wider mb-2"
              style={{ color: 'var(--text-dimmed)' }}
            >
              Properties
            </h3>
            <div className="space-y-1.5">
              {Object.entries(edge.properties).map(([key, val]) => (
                <div key={key} className="flex items-start gap-2">
                  <span className="text-[11px] font-mono shrink-0 w-20 text-right" style={{ color: 'var(--text-dimmed)' }}>
                    {key}
                  </span>
                  <span className="text-[11px] font-mono break-all" style={{ color: 'var(--text-secondary)' }}>
                    {String(val)}
                  </span>
                </div>
              ))}
            </div>
          </section>
        )}
      </div>
    </div>
  )
}
