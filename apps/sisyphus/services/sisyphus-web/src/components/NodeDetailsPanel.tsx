import { X } from 'lucide-react'
import type { GraphNode, TraverseResult } from '../types/graph'
import { STATUS_COLORS, getNodeColor } from '../types/graph'

interface NodeDetailsPanelProps {
  node: GraphNode | null
  traverseResult: TraverseResult | null
  onClose: () => void
}

const EDGE_TYPE_COLORS: Record<string, string> = {
  proves: 'var(--neon-green)',
  references: 'var(--neon-cyan)',
  translates: '#bf7fff',
  DEPENDS_ON: 'var(--accent-blue)',
  PRODUCES: 'var(--accent-green)',
  TRIGGERS: 'var(--accent-orange)',
  DEFAULT: 'var(--text-muted)',
}

/** Key properties to display prominently at top */
const KEY_PROPS = ['sisyphus_status', 'source_type', 'language', 'abstract'] as const

export default function NodeDetailsPanel({ node, traverseResult, onClose }: NodeDetailsPanelProps) {
  if (!node) return null

  const props = node.properties ?? {}
  const status = props.sisyphus_status as string | undefined
  const sourceType = props.source_type as string | undefined
  const language = props.language as string | undefined
  const abstract = props.abstract as string | undefined
  const body = props.body as string | undefined
  const sourceNodeId = props.source_node_id as string | undefined

  // Find translation edges
  const translationEdges = traverseResult?.edges.filter((e) => e.type === 'translates') ?? []
  const translationNodes = translationEdges.map((e) => {
    const otherId = e.source === node.id ? e.target : e.source
    return traverseResult?.neighbors.find((n) => n.id === otherId)
  }).filter(Boolean) as GraphNode[]

  // Remaining properties (excluding key ones already shown)
  const otherProps = Object.entries(props).filter(
    ([key]) => !KEY_PROPS.includes(key as any) && key !== 'body' && key !== 'name' && key !== 'source_node_id',
  )

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
        <div className="flex items-center gap-3">
          <span className="text-sm font-semibold truncate" style={{ color: 'var(--text-primary)' }}>
            {node.label}
          </span>
          {status && (
            <span
              className="badge text-[10px]"
              style={{
                color: STATUS_COLORS[status] ?? 'var(--text-muted)',
                borderColor: STATUS_COLORS[status] ?? 'var(--border-default)',
              }}
            >
              {status}
            </span>
          )}
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
          <div className="grid grid-cols-2 gap-2">
            <MetaCard label="Type" value={node.type} color={getNodeColor(node)} />
            <MetaCard label="Status" value={status ?? 'unknown'} color={STATUS_COLORS[status ?? ''] ?? 'var(--text-muted)'} />
            {sourceType && <MetaCard label="Source" value={sourceType} />}
            {language && <MetaCard label="Language" value={language} />}
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

        {/* Translation Links */}
        {translationNodes.length > 0 && (
          <section>
            <h3 className="text-[10px] font-semibold uppercase tracking-wider mb-2" style={{ color: 'var(--text-dimmed)' }}>
              Translations
            </h3>
            <div className="space-y-1">
              {translationNodes.map((tn) => (
                <div
                  key={tn.id}
                  className="flex items-center gap-2 px-2 py-1.5 rounded"
                  style={{ background: 'var(--bg-elevated)' }}
                >
                  <div className="w-2 h-2 rounded-full shrink-0" style={{ background: '#bf7fff' }} />
                  <span className="text-xs truncate flex-1" style={{ color: 'var(--text-secondary)' }}>
                    {tn.label}
                  </span>
                  <span className="text-[10px] font-mono" style={{ color: 'var(--text-dimmed)' }}>
                    {(tn.properties?.language as string) ?? ''}
                  </span>
                </div>
              ))}
            </div>
          </section>
        )}

        {/* Source Node Link (for translations) */}
        {sourceNodeId && (
          <section>
            <h3 className="text-[10px] font-semibold uppercase tracking-wider mb-2" style={{ color: 'var(--text-dimmed)' }}>
              Source Node
            </h3>
            <div className="text-[11px] font-mono" style={{ color: 'var(--accent-blue)' }}>
              {sourceNodeId}
            </div>
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
