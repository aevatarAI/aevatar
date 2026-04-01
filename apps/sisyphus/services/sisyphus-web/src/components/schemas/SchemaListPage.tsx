import { useState, useEffect, useCallback, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import { Plus, Loader2, Trash2, Box, GitBranch } from 'lucide-react'
import { useAuth } from '../../auth/useAuth'
import { fetchSchemas, deleteSchema } from '../../api/schemas-api'
import type { SchemaListItem } from '../../types/schema'
import ConfirmDialog from '../shared/ConfirmDialog'

function SchemaCard({ schema, onDelete, onClick }: {
  schema: SchemaListItem
  onDelete: (e: React.MouseEvent) => void
  onClick: () => void
}) {
  return (
    <div onClick={onClick} className="card card-hover cursor-pointer px-4 py-3 flex items-center gap-3 transition-all">
      <div className="flex-1 min-w-0">
        <div className="text-xs font-medium font-mono truncate" style={{ color: 'var(--text-primary)' }}>
          {schema.name}
        </div>
        {schema.description && (
          <div className="text-[10px] mt-0.5 truncate" style={{ color: 'var(--text-dimmed)' }}>
            {schema.description}
          </div>
        )}
      </div>
      <button onClick={onDelete} className="icon-btn shrink-0 p-1" title="Delete" style={{ color: 'var(--neon-red)' }}>
        <Trash2 size={12} />
      </button>
    </div>
  )
}

export default function SchemaListPage() {
  const { getAccessToken } = useAuth()
  const navigate = useNavigate()
  const [schemas, setSchemas] = useState<SchemaListItem[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<SchemaListItem | null>(null)
  const [deleting, setDeleting] = useState(false)

  const loadSchemas = useCallback(async () => {
    const token = getAccessToken()
    if (!token) return
    setLoading(true); setError(null)
    try { setSchemas(await fetchSchemas(token)) }
    catch (err) { setError(err instanceof Error ? err.message : 'Failed to load schemas') }
    finally { setLoading(false) }
  }, [getAccessToken])

  useEffect(() => { loadSchemas() }, [loadSchemas])

  const handleDeleteConfirm = useCallback(async () => {
    if (!deleteTarget) return
    const token = getAccessToken()
    if (!token) return
    setDeleting(true)
    try { await deleteSchema(deleteTarget.id, token); setSchemas((prev) => prev.filter((s) => s.id !== deleteTarget.id)); setDeleteTarget(null) }
    catch (err) { setError(err instanceof Error ? err.message : 'Delete failed'); setDeleteTarget(null) }
    finally { setDeleting(false) }
  }, [deleteTarget, getAccessToken])

  const nodeSchemas = useMemo(() => schemas.filter((s) => s.entityType === 'node'), [schemas])
  const edgeSchemas = useMemo(() => schemas.filter((s) => s.entityType === 'edge'), [schemas])

  return (
    <div className="h-full overflow-auto p-6">
      <div className="w-full">
        {/* Header */}
        <div className="flex items-center justify-between mb-6">
          <div>
            <h1 className="text-lg font-semibold" style={{ color: 'var(--text-primary)' }}>Schemas</h1>
            <p className="text-xs mt-1" style={{ color: 'var(--text-dimmed)' }}>JSON Schema definitions for chrono-graph nodes and edges</p>
          </div>
          <div className="flex items-center gap-2">
            <button onClick={() => navigate('/schemas/new/edit')} className="btn-neon-green text-xs gap-1.5 py-1.5 px-3">
              <Plus size={14} /> New Schema
            </button>
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

        {!loading && schemas.length === 0 && !error && (
          <div className="text-center py-12">
            <Box size={32} style={{ color: 'var(--text-dimmed)' }} className="mx-auto mb-3" />
            <p className="text-sm" style={{ color: 'var(--text-dimmed)' }}>No schemas defined yet</p>
          </div>
        )}

        {/* Two column layout: Nodes | Edges */}
        {!loading && schemas.length > 0 && (
          <div className="flex gap-6">
            {/* Node Schemas */}
            <div className="flex-1 min-w-0">
              <div className="flex items-center gap-2 mb-3">
                <Box size={14} style={{ color: 'var(--neon-cyan)' }} />
                <span className="text-xs font-semibold uppercase tracking-wider" style={{ color: 'var(--neon-cyan)' }}>
                  Node Schemas ({nodeSchemas.length})
                </span>
              </div>
              {nodeSchemas.length === 0 ? (
                <div className="card px-4 py-6 text-center">
                  <p className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>No node schemas</p>
                </div>
              ) : (
                <div className="space-y-1.5">
                  {nodeSchemas.map((s) => (
                    <SchemaCard key={s.id} schema={s}
                      onClick={() => navigate(`/schemas/${s.id}/edit`)}
                      onDelete={(e) => { e.stopPropagation(); setDeleteTarget(s) }} />
                  ))}
                </div>
              )}
            </div>

            {/* Edge Schemas */}
            <div className="flex-1 min-w-0">
              <div className="flex items-center gap-2 mb-3">
                <GitBranch size={14} style={{ color: 'var(--neon-gold)' }} />
                <span className="text-xs font-semibold uppercase tracking-wider" style={{ color: 'var(--neon-gold)' }}>
                  Edge Schemas ({edgeSchemas.length})
                </span>
              </div>
              {edgeSchemas.length === 0 ? (
                <div className="card px-4 py-6 text-center">
                  <p className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>No edge schemas</p>
                </div>
              ) : (
                <div className="space-y-1.5">
                  {edgeSchemas.map((s) => (
                    <SchemaCard key={s.id} schema={s}
                      onClick={() => navigate(`/schemas/${s.id}/edit`)}
                      onDelete={(e) => { e.stopPropagation(); setDeleteTarget(s) }} />
                  ))}
                </div>
              )}
            </div>
          </div>
        )}
      </div>

      {deleteTarget && (
        <ConfirmDialog
          title="Delete Schema"
          message={`Are you sure you want to delete <strong>${deleteTarget.name}</strong>? This action cannot be undone.`}
          loading={deleting}
          onConfirm={handleDeleteConfirm}
          onCancel={() => setDeleteTarget(null)}
        />
      )}
    </div>
  )
}
