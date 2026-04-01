import { useState, useEffect, useCallback, useRef } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { ArrowLeft, Save, Package, RefreshCw, Loader2, ChevronRight, Plus, Trash2 } from 'lucide-react'
import CodeMirror from '@uiw/react-codemirror'
import { json } from '@codemirror/lang-json'
import { useAuth } from '../../auth/useAuth'
import {
  fetchConnectors, createConnector, updateConnector,
  compileConnector, syncConnectors,
} from '../../api/workflow-api'
import type { ConnectorDefinition } from '../../types/workflow'
import Select from '../shared/Select'

type StageId = 'save' | 'compile' | 'sync'

const STAGES: Array<{ id: StageId; label: string; icon: typeof Save }> = [
  { id: 'save', label: 'Save', icon: Save },
  { id: 'compile', label: 'Compile', icon: Package },
  { id: 'sync', label: 'Sync to Aevatar', icon: RefreshCw },
]

const STAGE_STYLES = {
  active:   { bg: 'rgba(125,211,252,0.1)',  border: 'rgba(125,211,252,0.4)',  color: 'var(--neon-cyan)', opacity: 1 },
  done:     { bg: 'rgba(134,239,172,0.08)', border: 'rgba(134,239,172,0.25)', color: 'var(--neon-green)', opacity: 0.8 },
  disabled: { bg: 'transparent',           border: 'rgba(136,136,136,0.2)', color: '#666',    opacity: 0.4 },
  loading:  { bg: 'rgba(125,211,252,0.1)',  border: 'rgba(125,211,252,0.4)',  color: 'var(--neon-cyan)', opacity: 1 },
}

interface ConnectorForm {
  name: string
  description: string
  type: 'http' | 'mcp'
  baseUrl: string
  endpoints: Array<{ name: string; method: string; path: string }>
  mcpConfig: { serverName: string; command: string; arguments: string; allowedTools: string }
  authConfig: Record<string, unknown> | null
}

function formToJson(form: ConnectorForm): Record<string, unknown> {
  const obj: Record<string, unknown> = {
    name: form.name,
    description: form.description,
    type: form.type,
  }
  if (form.type === 'http') {
    obj.baseUrl = form.baseUrl
    obj.endpoints = form.endpoints.filter((e) => e.name || e.path)
    if (form.authConfig) obj.authConfig = form.authConfig
  }
  if (form.type === 'mcp') {
    obj.mcpConfig = {
      serverName: form.mcpConfig.serverName,
      command: form.mcpConfig.command,
      arguments: form.mcpConfig.arguments ? form.mcpConfig.arguments.split(',').map((s: string) => s.trim()).filter(Boolean) : [],
      allowedTools: form.mcpConfig.allowedTools ? form.mcpConfig.allowedTools.split(',').map((s: string) => s.trim()).filter(Boolean) : [],
    }
  }
  return obj
}

function jsonToForm(data: Record<string, unknown>): ConnectorForm {
  const mcp = (data.mcpConfig ?? {}) as Record<string, unknown>
  return {
    name: (data.name as string) ?? '',
    description: (data.description as string) ?? '',
    type: (data.type as 'http' | 'mcp') ?? 'http',
    baseUrl: (data.baseUrl as string) ?? '',
    endpoints: Array.isArray(data.endpoints) ? data.endpoints.map((e: Record<string, string>) => ({
      name: e.name ?? '', method: e.method ?? 'GET', path: e.path ?? '',
    })) : [],
    mcpConfig: {
      serverName: (mcp.serverName as string) ?? '',
      command: (mcp.command as string) ?? '',
      arguments: Array.isArray(mcp.arguments) ? (mcp.arguments as string[]).join(', ') : '',
      allowedTools: Array.isArray(mcp.allowedTools) ? (mcp.allowedTools as string[]).join(', ') : '',
    },
    authConfig: (data.authConfig as Record<string, unknown>) ?? null,
  }
}

const DEFAULT_FORM: ConnectorForm = {
  name: '', description: '', type: 'http', baseUrl: '',
  endpoints: [{ name: '', method: 'GET', path: '' }],
  mcpConfig: { serverName: '', command: '', arguments: '', allowedTools: '' },
  authConfig: null,
}

export default function ConnectorEditorPage() {
  const { id } = useParams<{ id: string }>()
  const isNew = id === 'new'
  const navigate = useNavigate()
  const { getAccessToken } = useAuth()

  const [connector, setConnector] = useState<ConnectorDefinition | null>(null)
  const [form, setForm] = useState<ConnectorForm>(DEFAULT_FORM)
  const [jsonContent, setJsonContent] = useState(JSON.stringify(formToJson(DEFAULT_FORM), null, 2))
  const [loading, setLoading] = useState(!isNew)
  const [loadingStage, setLoadingStage] = useState<StageId | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [jsonError, setJsonError] = useState<string | null>(null)
  const [dirty, setDirty] = useState(false)
  const [compiled, setCompiled] = useState(false)
  const [synced, setSynced] = useState(false)

  const lastEditSource = useRef<'ui' | 'json'>('ui')
  const [splitPct, setSplitPct] = useState(50)
  const containerRef = useRef<HTMLDivElement>(null)
  const dragging = useRef(false)

  // Load
  useEffect(() => {
    if (isNew || !id) return
    const token = getAccessToken()
    if (!token) return
    setLoading(true)
    fetchConnectors(token)
      .then((all) => {
        const found = all.find((c) => c.id === id)
        if (found) {
          setConnector(found)
          const f = jsonToForm(found as unknown as Record<string, unknown>)
          setForm(f)
          setJsonContent(JSON.stringify(found, null, 2))
        } else { setError('Connector not found') }
      })
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load'))
      .finally(() => setLoading(false))
  }, [id, isNew, getAccessToken])

  // UI → JSON
  useEffect(() => {
    if (lastEditSource.current !== 'ui') return
    const obj = formToJson(form)
    setJsonContent(JSON.stringify(obj, null, 2))
    setJsonError(null)
  }, [form])

  // JSON → UI
  const handleJsonChange = useCallback((value: string) => {
    lastEditSource.current = 'json'
    setJsonContent(value)
    setDirty(true); setCompiled(false); setSynced(false)
    try {
      const parsed = JSON.parse(value)
      setJsonError(null)
      setForm(jsonToForm(parsed))
    } catch (e) { setJsonError(e instanceof Error ? e.message : 'Invalid JSON') }
  }, [])

  const updateForm = useCallback((patch: Partial<ConnectorForm>) => {
    lastEditSource.current = 'ui'
    setForm((p) => ({ ...p, ...patch }))
    setDirty(true); setCompiled(false); setSynced(false)
  }, [])

  // Pipeline
  const handleSave = useCallback(async () => {
    const token = getAccessToken()
    if (!token) return
    let parsed: Record<string, unknown>
    try { parsed = JSON.parse(jsonContent) } catch { setError('Invalid JSON'); return }
    setLoadingStage('save'); setError(null)
    try {
      if (isNew) {
        const created = await createConnector(parsed, token)
        navigate(`/connectors/${created.id}/edit`, { replace: true })
      } else {
        const updated = await updateConnector(id!, parsed, token)
        setConnector(updated as ConnectorDefinition)
      }
      setDirty(false)
    } catch (err) { setError(err instanceof Error ? err.message : 'Save failed') }
    finally { setLoadingStage(null) }
  }, [jsonContent, isNew, id, getAccessToken, navigate])

  const handleCompile = useCallback(async () => {
    if (!connector) return
    const token = getAccessToken()
    if (!token) return
    setLoadingStage('compile'); setError(null)
    try {
      const result = await compileConnector(connector.id, token)
      const blob = new Blob([JSON.stringify(result, null, 2)], { type: 'application/json' })
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a'); a.href = url; a.download = `${connector.name}.connector.json`
      document.body.appendChild(a); a.click(); document.body.removeChild(a); URL.revokeObjectURL(url)
      setCompiled(true)
    } catch (err) { setError(err instanceof Error ? err.message : 'Compile failed') }
    finally { setLoadingStage(null) }
  }, [connector, getAccessToken])

  const handleSync = useCallback(async () => {
    const token = getAccessToken()
    if (!token) return
    setLoadingStage('sync'); setError(null)
    try { await syncConnectors(token); setSynced(true) }
    catch (err) { setError(err instanceof Error ? err.message : 'Sync failed') }
    finally { setLoadingStage(null) }
  }, [getAccessToken])

  const handlers: Record<StageId, () => void> = { save: handleSave, compile: handleCompile, sync: handleSync }
  function getStageState(stageId: StageId): string {
    if (loadingStage === stageId) return 'loading'
    switch (stageId) {
      case 'save': return dirty ? 'active' : 'done'
      case 'compile': return dirty || isNew ? 'disabled' : (compiled ? 'done' : 'active')
      case 'sync': return dirty || isNew ? 'disabled' : (synced ? 'done' : 'active')
    }
  }

  // Drag
  const handleMouseDown = useCallback(() => { dragging.current = true }, [])
  useEffect(() => {
    const move = (e: MouseEvent) => { if (!dragging.current || !containerRef.current) return; const r = containerRef.current.getBoundingClientRect(); setSplitPct(Math.max(30, Math.min(70, ((e.clientX - r.left) / r.width) * 100))) }
    const up = () => { dragging.current = false }
    window.addEventListener('mousemove', move); window.addEventListener('mouseup', up)
    return () => { window.removeEventListener('mousemove', move); window.removeEventListener('mouseup', up) }
  }, [])

  if (loading) return <div className="h-full flex items-center justify-center"><Loader2 size={20} className="animate-spin" style={{ color: 'var(--text-dimmed)' }} /></div>

  return (
    <div className="h-full flex flex-col">
      {/* Header */}
      <div className="flex items-center justify-between px-5 py-3 shrink-0" style={{ borderBottom: '1px solid var(--border-default)' }}>
        <div className="flex items-center gap-3">
          <button onClick={() => navigate('/workflows')} className="icon-btn"><ArrowLeft size={16} /></button>
          <span className="text-sm font-semibold" style={{ color: 'var(--text-primary)' }}>
            {isNew ? 'New Connector' : form.name || 'Edit Connector'}
          </span>
          <span className="badge badge-cyan text-[10px]">{form.type}</span>
          {dirty && <span className="text-[10px] px-1.5 py-0.5 rounded" style={{ background: 'rgba(252,211,77,0.15)', color: 'var(--neon-gold)' }}>unsaved</span>}
        </div>
        <div className="flex items-center gap-3">
          {error && <span className="text-[11px] max-w-[250px] truncate" style={{ color: 'var(--accent-red)' }}>{error}</span>}
          <div className="flex items-center gap-0">
            {STAGES.map((stage, idx) => {
              const state = getStageState(stage.id)
              const style = STAGE_STYLES[state as keyof typeof STAGE_STYLES] ?? STAGE_STYLES.disabled
              const Icon = stage.icon
              return (
                <div key={stage.id} className="flex items-center">
                  {idx > 0 && <ChevronRight size={14} style={{ color: 'var(--text-dimmed)', opacity: 0.3, margin: '0 2px' }} />}
                  <button onClick={handlers[stage.id]} disabled={state === 'disabled' || state === 'loading'}
                    className="flex items-center gap-1.5 px-3 py-1.5 rounded-md text-xs font-medium transition-all disabled:cursor-not-allowed"
                    style={{ background: style.bg, border: `1px solid ${style.border}`, color: style.color, opacity: style.opacity }}>
                    {state === 'loading' ? <Loader2 size={13} className="animate-spin" /> : <Icon size={13} />}
                    {stage.label}
                  </button>
                </div>
              )
            })}
          </div>
        </div>
      </div>

      {/* Split pane */}
      <div className="flex-1 flex overflow-hidden" ref={containerRef}>
        {/* Left: UI Form */}
        <div className="flex flex-col min-w-0 overflow-auto" style={{ width: `${splitPct}%` }}>
          <div className="p-5 space-y-5">
            {/* Basic info */}
            <div className="card p-5 space-y-4">
              <span className="text-[10px] font-semibold uppercase tracking-wider" style={{ color: 'var(--text-dimmed)' }}>Connector Info</span>
              <div className="grid grid-cols-[1fr_120px] gap-3">
                <label className="block">
                  <span className="text-[11px] font-medium" style={{ color: 'var(--text-secondary)' }}>Name</span>
                  <input className="input mt-1.5" value={form.name} onChange={(e) => updateForm({ name: e.target.value })} placeholder="e.g. chrono_graph" />
                </label>
                <label className="block">
                  <span className="text-[11px] font-medium" style={{ color: 'var(--text-secondary)' }}>Type</span>
                  <Select className="mt-1.5" value={form.type} options={[{ value: 'http', label: 'HTTP' }, { value: 'mcp', label: 'MCP' }]}
                    onChange={(v) => updateForm({ type: v as 'http' | 'mcp' })} />
                </label>
              </div>
              <label className="block">
                <span className="text-[11px] font-medium" style={{ color: 'var(--text-secondary)' }}>Description</span>
                <input className="input mt-1.5" value={form.description} onChange={(e) => updateForm({ description: e.target.value })} placeholder="What this connector does" />
              </label>
            </div>

            {/* HTTP config */}
            {form.type === 'http' && (
              <div className="card p-5 space-y-4 animate-expand">
                <span className="text-[10px] font-semibold uppercase tracking-wider" style={{ color: 'var(--text-dimmed)' }}>HTTP Configuration</span>
                <label className="block">
                  <span className="text-[11px] font-medium" style={{ color: 'var(--text-secondary)' }}>Base URL</span>
                  <input className="input mt-1.5 font-mono" value={form.baseUrl} onChange={(e) => updateForm({ baseUrl: e.target.value })} placeholder="https://api.example.com" />
                </label>

                {/* Endpoints */}
                <div className="space-y-2">
                  <div className="flex items-center justify-between">
                    <span className="text-[10px] font-semibold uppercase tracking-wider" style={{ color: 'var(--text-dimmed)' }}>Endpoints</span>
                    <button onClick={() => updateForm({ endpoints: [...form.endpoints, { name: '', method: 'GET', path: '' }] })}
                      className="btn-neon-green text-[10px] gap-1 py-1 px-2"><Plus size={10} /> Add</button>
                  </div>
                  {form.endpoints.map((ep, i) => (
                    <div key={i} className="flex items-center gap-2">
                      <input className="input text-xs flex-1" value={ep.name} placeholder="name"
                        onChange={(e) => { const eps = [...form.endpoints]; eps[i] = { ...ep, name: e.target.value }; updateForm({ endpoints: eps }) }} />
                      <Select className="w-24 shrink-0" value={ep.method}
                        options={['GET', 'POST', 'PUT', 'DELETE', 'PATCH']}
                        onChange={(v) => { const eps = [...form.endpoints]; eps[i] = { ...ep, method: v }; updateForm({ endpoints: eps }) }} />
                      <input className="input text-xs font-mono flex-1" value={ep.path} placeholder="/path"
                        onChange={(e) => { const eps = [...form.endpoints]; eps[i] = { ...ep, path: e.target.value }; updateForm({ endpoints: eps }) }} />
                      <button onClick={() => updateForm({ endpoints: form.endpoints.filter((_, idx) => idx !== i) })}
                        className="icon-btn shrink-0 p-1" style={{ color: 'var(--neon-red)' }}><Trash2 size={12} /></button>
                    </div>
                  ))}
                </div>
              </div>
            )}

            {/* MCP config */}
            {form.type === 'mcp' && (
              <div className="card p-5 space-y-4 animate-expand">
                <span className="text-[10px] font-semibold uppercase tracking-wider" style={{ color: 'var(--text-dimmed)' }}>MCP Configuration</span>
                <div className="grid grid-cols-2 gap-3">
                  <label className="block">
                    <span className="text-[11px] font-medium" style={{ color: 'var(--text-secondary)' }}>Server Name</span>
                    <input className="input mt-1.5" value={form.mcpConfig.serverName}
                      onChange={(e) => updateForm({ mcpConfig: { ...form.mcpConfig, serverName: e.target.value } })} placeholder="nyxid" />
                  </label>
                  <label className="block">
                    <span className="text-[11px] font-medium" style={{ color: 'var(--text-secondary)' }}>Command</span>
                    <input className="input mt-1.5 font-mono" value={form.mcpConfig.command}
                      onChange={(e) => updateForm({ mcpConfig: { ...form.mcpConfig, command: e.target.value } })} placeholder="npx" />
                  </label>
                </div>
                <label className="block">
                  <span className="text-[11px] font-medium" style={{ color: 'var(--text-secondary)' }}>Arguments (comma-separated)</span>
                  <input className="input mt-1.5 font-mono" value={form.mcpConfig.arguments}
                    onChange={(e) => updateForm({ mcpConfig: { ...form.mcpConfig, arguments: e.target.value } })} placeholder="--server, --port, 3000" />
                </label>
                <label className="block">
                  <span className="text-[11px] font-medium" style={{ color: 'var(--text-secondary)' }}>Allowed Tools (comma-separated)</span>
                  <input className="input mt-1.5 font-mono" value={form.mcpConfig.allowedTools}
                    onChange={(e) => updateForm({ mcpConfig: { ...form.mcpConfig, allowedTools: e.target.value } })} placeholder="get_snapshot, create_nodes" />
                </label>
              </div>
            )}
          </div>
        </div>

        {/* Divider */}
        <div onMouseDown={handleMouseDown}
          className="shrink-0 cursor-col-resize flex items-center justify-center hover:opacity-100 transition-opacity"
          style={{ width: 6, background: 'var(--border-default)', opacity: 0.6 }}>
          <div className="w-0.5 h-8 rounded" style={{ background: 'var(--text-dimmed)' }} />
        </div>

        {/* Right: JSON Editor */}
        <div className="min-w-0 flex flex-col" style={{ width: `${100 - splitPct}%` }}>
          <div className="flex items-center justify-between px-4 py-2 shrink-0" style={{ borderBottom: '1px solid var(--border-default)' }}>
            <span className="text-[11px] font-semibold uppercase tracking-wider" style={{ color: 'var(--text-dimmed)' }}>Connector JSON</span>
            {jsonError && <span className="text-[10px]" style={{ color: 'var(--accent-red)' }}>{jsonError}</span>}
          </div>
          <div className="flex-1 min-h-0">
            <CodeMirror value={jsonContent} onChange={handleJsonChange} extensions={[json()]} theme="dark" height="100%"
              basicSetup={{ lineNumbers: true, foldGutter: true, bracketMatching: true, closeBrackets: true }} />
          </div>
        </div>
      </div>
    </div>
  )
}
