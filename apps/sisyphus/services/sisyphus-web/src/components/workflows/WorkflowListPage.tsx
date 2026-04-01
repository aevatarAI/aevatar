import { useState, useEffect, useCallback } from 'react'
import { useNavigate, Link } from 'react-router-dom'
import { Plus, Trash2, GitBranch, Play, History, Loader2, Plug } from 'lucide-react'
import { useAuth } from '../../auth/useAuth'
import { fetchWorkflows, deleteWorkflow, createWorkflow, fetchConnectors, deleteConnector } from '../../api/workflow-api'
import type { WorkflowListItem, ConnectorDefinition } from '../../types/workflow'
import DeploymentBadge from './DeploymentBadge'
import ConfirmDialog from '../shared/ConfirmDialog'

export default function WorkflowListPage() {
  const { getAccessToken } = useAuth()
  const navigate = useNavigate()
  const [workflows, setWorkflows] = useState<WorkflowListItem[]>([])
  const [connectors, setConnectors] = useState<ConnectorDefinition[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const [newName, setNewName] = useState('')

  // Delete confirmation
  const [deleteTarget, setDeleteTarget] = useState<{ type: 'workflow' | 'connector'; id: string; name: string } | null>(null)

  // Connector state (minimal — editing now in dedicated page)

  const loadData = useCallback(async () => {
    const token = getAccessToken()
    if (!token) return
    setLoading(true)
    setError(null)
    try {
      const [wfData, connData] = await Promise.all([
        fetchWorkflows(token),
        fetchConnectors(token).catch(() => []),
      ])
      setWorkflows(wfData)
      setConnectors(connData)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load data')
    } finally {
      setLoading(false)
    }
  }, [getAccessToken])

  useEffect(() => {
    loadData()
  }, [loadData])

  const handleDeleteConfirm = useCallback(async () => {
    if (!deleteTarget) return
    const token = getAccessToken()
    if (!token) return
    try {
      if (deleteTarget.type === 'workflow') {
        await deleteWorkflow(deleteTarget.id, token)
        setWorkflows((prev) => prev.filter((w) => w.id !== deleteTarget.id))
      } else {
        await deleteConnector(deleteTarget.id, token)
        setConnectors((prev) => prev.filter((c) => c.id !== deleteTarget.id))
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Delete failed')
    } finally {
      setDeleteTarget(null)
    }
  }, [deleteTarget, getAccessToken])

  const handleCreate = useCallback(async () => {
    const token = getAccessToken()
    if (!token || !newName.trim()) return
    try {
      const wf = await createWorkflow({ name: newName.trim(), yaml: '# New workflow\nsteps:\n  step_1:\n    type: llm_call\n' }, token)
      setCreating(false)
      setNewName('')
      navigate(`/workflows/${wf.id}/edit`)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Create failed')
    }
  }, [newName, getAccessToken, navigate])

  const handleDeleteConnector = useCallback(
    (e: React.MouseEvent, connector: ConnectorDefinition) => {
      e.stopPropagation()
      setDeleteTarget({ type: 'connector', id: connector.id, name: connector.name })
    },
    [],
  )

  return (
    <div className="h-full overflow-auto p-6">
      <div className="w-full">
        {/* Header */}
        <div className="flex items-center justify-between mb-6">
          <div>
            <h1 className="text-lg font-semibold" style={{ color: 'var(--text-primary)' }}>
              Workflows
            </h1>
            <p className="text-xs mt-1" style={{ color: 'var(--text-dimmed)' }}>
              Manage workflow definitions and run workflows
            </p>
          </div>
          <div className="flex items-center gap-2">
            <Link to="/workflows/history" className="btn-secondary text-xs gap-1.5 py-1.5 px-3">
              <History size={14} />
              History
            </Link>
            <Link to="/workflows/run" className="btn-neon text-xs gap-1.5 py-1.5 px-3">
              <Play size={14} />
              Run Workflow
            </Link>
            <button onClick={() => setCreating(true)} className="btn-neon-green text-xs gap-1.5 py-1.5 px-3">
              <Plus size={14} />
              New
            </button>
          </div>
        </div>

        {/* Create dialog */}
        {creating && (
          <div
            className="mb-4 flex items-center gap-2 px-4 py-3 rounded-lg"
            style={{ background: 'var(--bg-surface)', border: '1px solid var(--border-default)' }}
          >
            <input
              className="input flex-1"
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              placeholder="Workflow name"
              autoFocus
              onKeyDown={(e) => e.key === 'Enter' && handleCreate()}
            />
            <button onClick={handleCreate} className="btn-neon-green text-xs py-1.5 px-3">Create</button>
            <button onClick={() => { setCreating(false); setNewName('') }} className="btn-secondary text-xs py-1.5 px-3">Cancel</button>
          </div>
        )}

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

        {!loading && workflows.length === 0 && !error && (
          <div className="text-center py-12">
            <GitBranch size={32} style={{ color: 'var(--text-dimmed)' }} className="mx-auto mb-3" />
            <p className="text-sm" style={{ color: 'var(--text-dimmed)' }}>No workflows defined yet</p>
          </div>
        )}

        {/* Workflow List */}
        <div className="space-y-2">
          {workflows.map((wf) => (
            <div
              key={wf.id}
              className="card card-hover flex items-center gap-4 px-4 py-3 cursor-pointer"
              onClick={() => navigate(`/workflows/${wf.id}/edit`)}
            >
              <GitBranch size={16} style={{ color: 'var(--accent-gold)' }} className="shrink-0" />
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-medium truncate" style={{ color: 'var(--text-primary)' }}>
                    {wf.name}
                  </span>
                  <DeploymentBadge status={wf.deploymentState?.status} />
                </div>
                {wf.description && (
                  <div className="text-[11px] mt-0.5 truncate" style={{ color: 'var(--text-dimmed)' }}>
                    {wf.description}
                  </div>
                )}
              </div>
              <button
                onClick={(e) => { e.stopPropagation(); setDeleteTarget({ type: 'workflow', id: wf.id, name: wf.name }) }}
                className="icon-btn shrink-0"
                title="Delete"
                style={{ color: 'var(--accent-red)' }}
              >
                <Trash2 size={14} />
              </button>
            </div>
          ))}
        </div>

        {/* Connectors Section */}
        <div className="mt-10">
          <div className="flex items-center justify-between mb-3">
            <h2 className="text-sm font-semibold flex items-center gap-2" style={{ color: 'var(--text-primary)' }}>
              <Plug size={15} />
              Connectors
            </h2>
            <button
              onClick={() => navigate('/connectors/new/edit')}
              className="btn-neon-green text-xs gap-1.5 py-1 px-2.5"
            >
              <Plus size={12} />
              New
            </button>
          </div>
          {connectors.length === 0 && !loading && (
            <div className="text-center py-8">
              <Plug size={24} style={{ color: 'var(--text-dimmed)' }} className="mx-auto mb-2" />
              <p className="text-xs" style={{ color: 'var(--text-dimmed)' }}>No connectors defined</p>
            </div>
          )}
          <div className="grid grid-cols-3 gap-3">
            {connectors.map((c) => (
              <div
                key={c.id}
                className="card card-hover cursor-pointer p-3"
                onClick={() => navigate(`/connectors/${c.id}/edit`)}
              >
                <div className="flex items-start justify-between mb-2">
                  <div className="flex items-center gap-2">
                    <Plug size={14} style={{ color: 'var(--accent-blue)' }} />
                    <span className="text-xs font-medium" style={{ color: 'var(--text-primary)' }}>{c.name}</span>
                  </div>
                  <button
                    onClick={(e) => handleDeleteConnector(e, c)}
                    className="icon-btn shrink-0"
                    title="Delete"
                    style={{ color: 'var(--accent-red)' }}
                  >
                    <Trash2 size={12} />
                  </button>
                </div>
                <span
                  className="text-[10px] font-medium uppercase px-2 py-0.5 rounded"
                  style={{
                    background: c.type === 'http' ? 'rgba(125,211,252,0.1)' : 'rgba(196,181,253,0.1)',
                    color: c.type === 'http' ? 'var(--accent-blue)' : 'var(--neon-purple)',
                    border: `1px solid ${c.type === 'http' ? 'rgba(125,211,252,0.2)' : 'rgba(196,181,253,0.2)'}`,
                  }}
                >
                  {c.type}
                </span>
              </div>
            ))}
          </div>
        </div>
      </div>

      {deleteTarget && (
        <ConfirmDialog
          title={`Delete ${deleteTarget.type === 'workflow' ? 'Workflow' : 'Connector'}`}
          message={`Are you sure you want to delete <strong>${deleteTarget.name}</strong>? This action cannot be undone.`}
          onConfirm={handleDeleteConfirm}
          onCancel={() => setDeleteTarget(null)}
        />
      )}
    </div>
  )
}
