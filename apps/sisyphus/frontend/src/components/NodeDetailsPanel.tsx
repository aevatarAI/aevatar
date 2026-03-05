import { X } from 'lucide-react'
import type { GraphNode, TraverseResult } from '../types/graph'

interface NodeDetailsPanelProps {
  node: GraphNode | null
  traverseResult: TraverseResult | null
  onClose: () => void
}

const EDGE_TYPE_COLORS: Record<string, string> = {
  DEPENDS_ON: 'var(--accent-blue)',
  PRODUCES: 'var(--accent-green)',
  TRIGGERS: 'var(--accent-orange)',
  DEFAULT: 'var(--text-muted)',
}

export default function NodeDetailsPanel({ node, traverseResult, onClose }: NodeDetailsPanelProps) {
  if (!node) return null

  return (
    <div
      className="absolute right-0 top-0 bottom-0 flex flex-col overflow-hidden z-20 animate-slide-in"
      style={{
        width: '66vw',
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
        <span className="text-sm font-semibold truncate" style={{ color: 'var(--text-primary)' }}>
          {node.label}
        </span>
        <button
          onClick={onClose}
          className="icon-btn"
        >
          <X size={16} />
        </button>
      </div>

      {/* Body */}
      <div className="flex-1 overflow-y-auto p-4 space-y-5">
        {/* Properties */}
        <section>
          <h3
            className="text-[10px] font-semibold uppercase tracking-wider mb-2"
            style={{ color: 'var(--text-dimmed)' }}
          >
            Properties
          </h3>
          <div className="space-y-1.5">
            <PropertyRow label="ID" value={node.id} />
            <PropertyRow label="Type" value={node.type} />
            {node.properties &&
              Object.entries(node.properties).map(([key, val]) => (
                <PropertyRow key={key} label={key} value={String(val)} />
              ))}
          </div>
        </section>

        {/* Connected Nodes */}
        {traverseResult && traverseResult.neighbors.length > 0 && (
          <section>
            <h3
              className="text-[10px] font-semibold uppercase tracking-wider mb-2"
              style={{ color: 'var(--text-dimmed)' }}
            >
              Connected Nodes
            </h3>
            <div className="space-y-1">
              {traverseResult.neighbors.map((neighbor) => {
                const edge = traverseResult.edges.find(
                  (e) =>
                    (e.source === node.id && e.target === neighbor.id) ||
                    (e.target === node.id && e.source === neighbor.id),
                )
                return (
                  <div
                    key={neighbor.id}
                    className="flex items-center gap-2 px-2 py-1.5 rounded"
                    style={{ background: 'var(--bg-elevated)' }}
                  >
                    <div
                      className="w-2 h-2 rounded-full shrink-0"
                      style={{ background: 'var(--accent-blue)' }}
                    />
                    <span className="text-xs truncate flex-1" style={{ color: 'var(--text-secondary)' }}>
                      {neighbor.label}
                    </span>
                    {edge && (
                      <span
                        className="text-[10px] font-mono px-1.5 py-0.5 rounded shrink-0"
                        style={{
                          color: EDGE_TYPE_COLORS[edge.type] ?? EDGE_TYPE_COLORS.DEFAULT,
                          background: 'var(--bg-accent)',
                        }}
                      >
                        {edge.type}
                      </span>
                    )}
                  </div>
                )
              })}
            </div>
          </section>
        )}

        {traverseResult && traverseResult.neighbors.length === 0 && (
          <p className="text-xs" style={{ color: 'var(--text-dimmed)' }}>
            No connected nodes found.
          </p>
        )}
      </div>
    </div>
  )
}

function PropertyRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-start gap-2">
      <span
        className="text-[11px] font-mono shrink-0 w-20 text-right"
        style={{ color: 'var(--text-dimmed)' }}
      >
        {label}
      </span>
      <span
        className="text-[11px] font-mono break-all"
        style={{ color: 'var(--text-secondary)' }}
      >
        {value}
      </span>
    </div>
  )
}
