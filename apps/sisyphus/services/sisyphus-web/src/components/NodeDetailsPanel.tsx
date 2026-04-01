import { X } from 'lucide-react'
import type { GraphNode, TraverseResult } from '../types/graph'
import { getNodeColor, getEdgeColor } from '../types/graph'

interface NodeDetailsPanelProps {
  node: GraphNode | null
  traverseResult: TraverseResult | null
  onClose: () => void
}


export default function NodeDetailsPanel({ node, traverseResult, onClose }: NodeDetailsPanelProps) {
  if (!node) return null

  // Merge: use traverse result's full node data if available, fallback to lightweight node
  const fullNode = traverseResult?.node ?? node
  const props = fullNode.properties ?? node.properties ?? {}
  const abstract = typeof props.abstract === 'string' ? props.abstract : undefined
  const body = typeof props.body === 'string' ? props.body : undefined
  const rawData = typeof props.raw_data === 'string' ? props.raw_data : undefined

  // All properties for display (exclude known display fields)
  const SKIP_KEYS = new Set(['abstract', 'body', 'raw_data', 'id', 'graphId', 'type', 'createdAt', 'updatedAt', 'createdBy', 'updatedBy'])
  const otherProps = Object.entries(props).filter(([key]) => !SKIP_KEYS.has(key))

  return (
    <div
      className="absolute right-0 top-0 bottom-0 flex flex-col overflow-hidden z-40 animate-slide-in"
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
        <div className="flex items-center gap-3">
          <span className="text-sm font-semibold truncate" style={{ color: 'var(--text-primary)' }}>
            {node.label}
          </span>
        </div>
        <button onClick={onClose} className="icon-btn">
          <X size={16} />
        </button>
      </div>

      {/* Body */}
      <div className="flex-1 overflow-y-auto p-4 space-y-5">
        {/* Key Metadata */}
        <section>
          <h3 className="text-[10px] font-semibold uppercase tracking-wider mb-2" style={{ color: 'var(--text-dimmed)' }}>
            Metadata
          </h3>
          <div className="grid grid-cols-1 gap-2">
            <MetaCard label="Type" value={node.type} color={getNodeColor(node)} />
          </div>
          <div className="mt-2">
            <PropertyRow label="ID" value={node.id} />
          </div>
        </section>

        {/* Abstract */}
        {abstract && (
          <section>
            <h3 className="text-[10px] font-semibold uppercase tracking-wider mb-2" style={{ color: 'var(--text-dimmed)' }}>
              Abstract
            </h3>
            <div
              className="px-3 py-2 rounded text-[12px] leading-relaxed"
              style={{ background: 'var(--bg-elevated)', color: 'var(--text-secondary)' }}
            >
              {abstract}
            </div>
          </section>
        )}

        {/* Body (LaTeX) */}
        {body && (
          <section>
            <h3 className="text-[10px] font-semibold uppercase tracking-wider mb-2" style={{ color: 'var(--text-dimmed)' }}>
              Body (LaTeX)
            </h3>
            <pre
              className="px-3 py-2 rounded text-[11px] font-mono overflow-x-auto whitespace-pre-wrap leading-relaxed"
              style={{ background: 'var(--bg-base)', color: 'var(--text-secondary)', border: '1px solid var(--border-subtle)', maxHeight: 300 }}
            >
              {body}
            </pre>
          </section>
        )}

        {/* Raw Data (for raw nodes) */}
        {rawData && (
          <section>
            <h3 className="text-[10px] font-semibold uppercase tracking-wider mb-2" style={{ color: 'var(--text-dimmed)' }}>
              Raw Data (LaTeX)
            </h3>
            <pre
              className="px-3 py-2 rounded text-[11px] font-mono overflow-x-auto whitespace-pre-wrap leading-relaxed"
              style={{ background: 'var(--bg-base)', color: 'var(--text-secondary)', border: '1px solid var(--border-subtle)', maxHeight: 300 }}
            >
              {rawData}
            </pre>
          </section>
        )}

        {/* Other Properties */}
        {otherProps.length > 0 && (
          <section>
            <h3 className="text-[10px] font-semibold uppercase tracking-wider mb-2" style={{ color: 'var(--text-dimmed)' }}>
              Properties
            </h3>
            <div className="space-y-1.5">
              {otherProps.map(([key, val]) => (
                <PropertyRow key={key} label={key} value={String(val)} />
              ))}
            </div>
          </section>
        )}

        {/* Connected Nodes */}
        {traverseResult && traverseResult.neighbors.length > 0 && (
          <section>
            <h3 className="text-[10px] font-semibold uppercase tracking-wider mb-2" style={{ color: 'var(--text-dimmed)' }}>
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
                      style={{ background: getNodeColor(neighbor) }}
                    />
                    <span className="text-xs truncate flex-1" style={{ color: 'var(--text-secondary)' }}>
                      {neighbor.label}
                    </span>
                    {edge && (
                      <span
                        className="text-[10px] font-mono px-1.5 py-0.5 rounded shrink-0"
                        style={{
                          color: getEdgeColor(edge),
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

        {!traverseResult && (
          <div className="flex items-center gap-2 py-2">
            <div className="w-3 h-3 border border-t-transparent rounded-full animate-spin" style={{ borderColor: 'var(--neon-cyan)', borderTopColor: 'transparent' }} />
            <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Loading connections...</span>
          </div>
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

function MetaCard({ label, value, color }: { label: string; value: string; color?: string }) {
  return (
    <div className="px-3 py-2 rounded" style={{ background: 'var(--bg-elevated)' }}>
      <div className="text-[10px] font-semibold uppercase tracking-wider" style={{ color: 'var(--text-dimmed)' }}>
        {label}
      </div>
      <div className="text-xs font-medium mt-0.5" style={{ color: color ?? 'var(--text-secondary)' }}>
        {value}
      </div>
    </div>
  )
}

function PropertyRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-start gap-2">
      <span className="text-[11px] font-mono shrink-0 w-20 text-right" style={{ color: 'var(--text-dimmed)' }}>
        {label}
      </span>
      <span className="text-[11px] font-mono break-all" style={{ color: 'var(--text-secondary)' }}>
        {value}
      </span>
    </div>
  )
}
