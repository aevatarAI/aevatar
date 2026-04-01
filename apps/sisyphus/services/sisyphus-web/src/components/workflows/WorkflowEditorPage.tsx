import { useState, useEffect, useCallback, useRef } from 'react'
import { useParams, useNavigate, Link } from 'react-router-dom'
import {
  Loader2, ArrowLeft, Square, Clock, CheckCircle2,
  AlertCircle, Eye, History as HistoryIcon, RefreshCw,
  Plus, Trash2, ChevronDown, ChevronRight, FileCode, Users, Layers, GripVertical,
} from 'lucide-react'
import yaml from 'js-yaml'
import CodeMirror from '@uiw/react-codemirror'
import { StreamLanguage } from '@codemirror/language'
import { yaml as yamlMode } from '@codemirror/legacy-modes/mode/yaml'
import { useAuth } from '../../auth/useAuth'
import { proxyUrl } from '../../hooks/use-api'
import { fetchWorkflow, updateWorkflow, fetchConnectors, compileWorkflow, deployWorkflow } from '../../api/workflow-api'
import { fetchTriggerHistory, stopWorkflowRun } from '../../api/runner-api'
import type { TriggerHistoryItem } from '../../types/runner'
import type { WorkflowDefinition, ConnectorDefinition } from '../../types/workflow'
import WorkflowVisualizer from './WorkflowVisualizer'
import DeploymentBadge from './DeploymentBadge'
import WorkflowPipeline, { type StageId } from './WorkflowPipeline'
import { refHighlightPlugin, refHighlightTheme } from '../../utils/yaml-ref-highlight'
import { DndContext, closestCenter, PointerSensor, useSensor, useSensors, type DragEndEvent } from '@dnd-kit/core'
import { SortableContext, verticalListSortingStrategy, useSortable, arrayMove } from '@dnd-kit/sortable'
import { CSS } from '@dnd-kit/utilities'
import Select from '../shared/Select'
import SlashCommandTextarea from '../shared/SlashCommandTextarea'

// --- Types ---

interface RoleForm { id: string; name: string; skillId: string; system_prompt: string }
interface StepForm { id: string; type: string; role?: string; connector?: string; parameters: Record<string, string>; children?: StepForm[] }
interface WorkflowForm { name: string; description: string; roles: RoleForm[]; steps: StepForm[] }

const STEP_TYPES = ['while', 'llm_call', 'connector_call', 'transform', 'parallel', 'workflow_call', 'foreach']

// --- Converters ---

function yamlToForm(yamlStr: string): WorkflowForm | null {
  try {
    const doc = yaml.load(yamlStr) as Record<string, unknown>
    if (!doc || typeof doc !== 'object') return null
    const roles = ((doc.roles ?? []) as Record<string, unknown>[]).map((r) => ({
      id: (r.id as string) ?? (r.name as string) ?? '',
      name: (r.name as string) ?? '',
      skillId: (r.skillId as string) ?? (r.skill_id as string) ?? '',
      system_prompt: (r.system_prompt as string) ?? '',
    }))
    const parseSteps = (steps: Record<string, unknown>[]): StepForm[] =>
      steps.map((s) => {
        const params = (s.parameters ?? {}) as Record<string, unknown>
        return {
          id: (s.id as string) ?? (s.name as string) ?? '',
          type: (s.type as string) ?? 'llm_call',
          role: (s.role as string) ?? undefined,
          connector: (params.connector as string) ?? (s.connector as string) ?? undefined,
          parameters: Object.fromEntries(
            Object.entries(params).filter(([k]) => k !== 'connector').map(([k, v]) => [k, typeof v === 'string' ? v : JSON.stringify(v)]),
          ),
          children: Array.isArray(s.children) ? parseSteps(s.children) : undefined,
        }
      })
    return {
      name: (doc.name as string) ?? '',
      description: (doc.description as string) ?? '',
      roles,
      steps: parseSteps((doc.steps ?? []) as Record<string, unknown>[]),
    }
  } catch { return null }
}

function formToYaml(form: WorkflowForm): string {
  const buildSteps = (steps: StepForm[]): Record<string, unknown>[] =>
    steps.map((s) => {
      const step: Record<string, unknown> = { id: s.id, type: s.type }
      if (s.role) step.role = s.role.toLowerCase()
      // connector goes inside parameters (aevatar RawStep has no top-level connector field)
      const params: Record<string, unknown> = {}
      if (s.connector) params.connector = s.connector
      for (const [k, v] of Object.entries(s.parameters)) {
        if (!v || k === 'connector') continue // skip if already handled
        try { params[k] = JSON.parse(v) } catch { params[k] = v }
      }
      if (Object.keys(params).length > 0) step.parameters = params
      if (s.children?.length) step.children = buildSteps(s.children)
      return step
    })

  const doc: Record<string, unknown> = {
    name: form.name,
    description: form.description,
    roles: form.roles.length === 0 ? [] : form.roles.map((r) => {
      const role: Record<string, unknown> = { id: r.id.toLowerCase(), name: r.name }
      if (r.skillId) role.skillId = r.skillId
      if (r.system_prompt) role.system_prompt = r.system_prompt
      return role
    }),
    steps: buildSteps(form.steps),
  }
  return yaml.dump(doc, { lineWidth: 120, noRefs: true })
}

// --- Helpers ---

function statusColor(status: string): string {
  if (status === 'completed') return 'var(--neon-green)'
  if (status === 'failed') return 'var(--neon-red)'
  if (status === 'running') return 'var(--neon-gold)'
  return 'var(--text-dimmed)'
}

function StatusIcon({ status }: { status: string }) {
  if (status === 'completed') return <CheckCircle2 size={13} style={{ color: statusColor(status) }} />
  if (status === 'failed') return <AlertCircle size={13} style={{ color: statusColor(status) }} />
  if (status === 'running') return <Loader2 size={13} className="animate-spin" style={{ color: statusColor(status) }} />
  return <Clock size={13} style={{ color: statusColor(status) }} />
}

type RightTab = 'yaml' | 'visualizer' | 'runs'

// --- Sortable Step Component ---

function SortableStep({ id, step, expanded, onToggleExpand, roles, onUpdate, onDelete, onUpdateParams }: {
  id: string; step: StepForm; expanded: boolean
  onToggleExpand: () => void; roles: RoleForm[]
  onUpdate: (patch: Partial<StepForm>) => void; onDelete: () => void
  onUpdateParams: (params: Record<string, string>) => void
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id })
  const style = { transform: CSS.Transform.toString(transform), transition, opacity: isDragging ? 0.5 : 1, zIndex: isDragging ? 50 : undefined }

  return (
    <div ref={setNodeRef} style={style} className="card overflow-hidden transition-shadow" {...attributes}>
      <div className="flex items-center gap-2 px-4 py-2.5">
        <div {...listeners} className="cursor-grab active:cursor-grabbing shrink-0 touch-none" style={{ color: 'var(--text-dimmed)' }}>
          <GripVertical size={14} />
        </div>
        <button onClick={onToggleExpand} style={{ color: 'var(--text-dimmed)' }}>
          {expanded ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
        </button>
        <input className="input text-sm font-mono flex-1 py-1.5" value={step.id} placeholder="step_id"
          onChange={(e) => onUpdate({ id: e.target.value })} />
        <Select className="w-36 shrink-0" value={step.type} options={STEP_TYPES}
          onChange={(v) => onUpdate({ type: v })} />
        <button onClick={onDelete} className="icon-btn p-1" style={{ color: 'var(--neon-red)' }}><Trash2 size={12} /></button>
      </div>
      {expanded && (
        <div className="px-4 pb-4 pt-2 space-y-3 animate-expand" style={{ borderTop: '1px solid var(--border-subtle)', background: 'var(--bg-base)' }}>
          <div className="grid grid-cols-2 gap-3">
            <label className="block">
              <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Role</span>
              <Select className="mt-1" value={step.role ?? ''} placeholder="(none)"
                options={['', ...roles.map((r) => r.id || r.name)]}
                onChange={(v) => onUpdate({ role: v || undefined })} />
            </label>
            <label className="block">
              <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Connector</span>
              <input className="input mt-1 font-mono" value={step.connector ?? ''} placeholder="(none)"
                onChange={(e) => onUpdate({ connector: e.target.value || undefined })} />
            </label>
          </div>
          <div className="space-y-1.5">
            <div className="flex items-center justify-between">
              <span className="text-[10px] font-semibold uppercase" style={{ color: 'var(--text-dimmed)' }}>Parameters</span>
              <button onClick={() => onUpdateParams({ ...step.parameters, '': '' })}
                className="text-[9px] px-1.5 py-0.5 rounded" style={{ color: 'var(--neon-cyan)', border: '1px solid rgba(125,211,252,0.3)' }}>+ Add</button>
            </div>
            {Object.entries(step.parameters).map(([key, val], pi) => {
              const isPromptPrefix = step.type === 'llm_call' && key === 'prompt_prefix'
              return (
                <div key={pi} className={isPromptPrefix ? 'space-y-1' : 'flex items-center gap-2'}>
                  <div className={isPromptPrefix ? 'flex items-center gap-2' : 'contents'}>
                    <input className="input text-xs font-mono w-1/3 py-1.5" value={key} placeholder="key"
                      onChange={(e) => {
                        const entries = Object.entries(step.parameters); entries[pi] = [e.target.value, val]
                        onUpdateParams(Object.fromEntries(entries))
                      }} />
                    {!isPromptPrefix && (
                      <input className="input text-xs font-mono flex-1 py-1.5" value={val} placeholder="value"
                        onChange={(e) => {
                          const entries = Object.entries(step.parameters); entries[pi] = [key, e.target.value]
                          onUpdateParams(Object.fromEntries(entries))
                        }} />
                    )}
                    <button onClick={() => {
                      onUpdateParams(Object.fromEntries(Object.entries(step.parameters).filter((_, idx) => idx !== pi)))
                    }} className="icon-btn p-1" style={{ color: 'var(--neon-red)' }}><Trash2 size={10} /></button>
                  </div>
                  {isPromptPrefix && (
                    <SlashCommandTextarea value={val} rows={3}
                      onChange={(v) => {
                        const entries = Object.entries(step.parameters); entries[pi] = [key, v]
                        onUpdateParams(Object.fromEntries(entries))
                      }}
                      placeholder="Prompt prefix — type / for skills & schemas" />
                  )}
                </div>
              )
            })}
          </div>
        </div>
      )}
    </div>
  )
}

// --- Component ---

export default function WorkflowEditorPage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const { getAccessToken } = useAuth()

  const [workflow, setWorkflow] = useState<WorkflowDefinition | null>(null)
  const [form, setForm] = useState<WorkflowForm>({ name: '', description: '', roles: [], steps: [] })
  const [yamlContent, setYamlContent] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [dirty, setDirty] = useState(false)
  const [rightTab, setRightTab] = useState<RightTab>('yaml')
  const [loadingStage, setLoadingStage] = useState<StageId | null>(null)

  // Response popup
  const [popup, setPopup] = useState<{ type: 'success' | 'error'; title: string; message: string; downloadYaml?: string; downloadName?: string } | null>(null)

  // Sync progress popup
  type SyncStep = { label: string; status: 'pending' | 'running' | 'done' | 'error'; error?: string }
  const [syncSteps, setSyncSteps] = useState<SyncStep[] | null>(null)

  // Expanded states
  const [expandedRole, setExpandedRole] = useState<number | null>(null)
  const [expandedStep, setExpandedStep] = useState<number | null>(null)

  // dnd-kit sensors
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 5 } }))

  const handleDragEnd = useCallback((event: DragEndEvent) => {
    const { active, over } = event
    if (!over || active.id === over.id) return
    lastEditSource.current = 'ui'
    setForm((prev) => {
      const oldIdx = prev.steps.findIndex((s) => s.id === active.id)
      const newIdx = prev.steps.findIndex((s) => s.id === over.id)
      if (oldIdx === -1 || newIdx === -1) return prev
      return { ...prev, steps: arrayMove(prev.steps, oldIdx, newIdx) }
    })
    setDirty(true)
  }, [])

  // Runs
  const [runs, setRuns] = useState<TriggerHistoryItem[]>([])
  const [runsLoading, setRunsLoading] = useState(false)

  // Split pane
  const [splitPct, setSplitPct] = useState(45)
  const containerRef = useRef<HTMLDivElement>(null)
  const dragging = useRef(false)
  const lastEditSource = useRef<'ui' | 'yaml'>('ui')

  // Connectors for visualizer
  const [connectorDefs, setConnectorDefs] = useState<ConnectorDefinition[]>([])

  // Load
  useEffect(() => {
    if (!id) return
    const token = getAccessToken()
    if (!token) return
    setLoading(true)
    fetchWorkflow(id, token)
      .then((wf) => {
        setWorkflow(wf)
        if (wf.yaml) {
          // YAML-based workflow — parse YAML to form
          setYamlContent(wf.yaml)
          const parsed = yamlToForm(wf.yaml)
          if (parsed) setForm(parsed)
        } else if (wf.roles?.length || wf.steps?.length) {
          // Structured workflow — build form from fields, generate YAML
          const f: WorkflowForm = {
            name: wf.name ?? '',
            description: wf.description ?? '',
            roles: (wf.roles ?? []).map((r) => ({
              id: r.name, name: r.name,
              skillId: r.skillId ?? '',
              system_prompt: r.description ?? '',
            })),
            steps: (wf.steps ?? []).map((s) => ({
              id: s.name, type: s.type,
              role: s.roleRef, connector: s.connectorRef,
              parameters: Object.fromEntries(
                Object.entries(s.parameters ?? {}).map(([k, v]) => [k, typeof v === 'string' ? v : JSON.stringify(v)]),
              ),
            })),
          }
          setForm(f)
          lastEditSource.current = 'ui' // trigger YAML generation
        }
      })
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load'))
      .finally(() => setLoading(false))
  }, [id, getAccessToken])

  useEffect(() => {
    const token = getAccessToken()
    if (!token) return
    fetchConnectors(token).then(setConnectorDefs).catch(() => {})
  }, [getAccessToken])

  // UI → YAML sync
  useEffect(() => {
    if (lastEditSource.current !== 'ui') return
    const newYaml = formToYaml(form)
    setYamlContent(newYaml)
  }, [form])

  // YAML → UI sync
  const handleYamlChange = useCallback((value: string) => {
    lastEditSource.current = 'yaml'
    setYamlContent(value)
    setDirty(true)
    const parsed = yamlToForm(value)
    if (parsed) setForm(parsed)
  }, [])

  const updateForm = useCallback((patch: Partial<WorkflowForm>) => {
    lastEditSource.current = 'ui'
    setForm((p) => ({ ...p, ...patch }))
    setDirty(true)
  }, [])

  // Reload workflow
  const reloadWorkflow = useCallback(async () => {
    if (!id) return
    const token = getAccessToken()
    if (!token) return
    try { const wf = await fetchWorkflow(id, token); setWorkflow(wf) } catch {}
  }, [id, getAccessToken])

  // Pipeline handlers
  const handleSave = useCallback(async () => {
    if (!id) return
    const token = getAccessToken()
    if (!token) return
    setLoadingStage('save'); setError(null)
    try {
      await updateWorkflow(id, { yaml: yamlContent }, token)
      setDirty(false); await reloadWorkflow()
      setPopup({ type: 'success', title: 'Saved', message: 'Workflow saved successfully.' })
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Save failed'
      setPopup({ type: 'error', title: 'Save Failed', message: msg })
    }
    finally { setLoadingStage(null) }
  }, [id, yamlContent, getAccessToken, reloadWorkflow])

  const handleCompile = useCallback(async () => {
    if (!id) return
    const token = getAccessToken()
    if (!token) return
    setLoadingStage('compile'); setError(null)
    try {
      const result = await compileWorkflow(id, token)
      await reloadWorkflow()
      setPopup({
        type: 'success', title: 'Compiled',
        message: `Workflow compiled successfully (${result.workflowYaml.length} bytes).`,
        downloadYaml: result.workflowYaml,
        downloadName: `${workflow?.name ?? id}.yaml`,
      })
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Compile failed'
      setPopup({ type: 'error', title: 'Compile Failed', message: msg })
    }
    finally { setLoadingStage(null) }
  }, [id, workflow?.name, getAccessToken, reloadWorkflow])

  const handleDeploy = useCallback(async () => {
    if (!id) return
    const token = getAccessToken()
    if (!token) return
    setLoadingStage('deploy'); setError(null)

    const steps: SyncStep[] = [
      { label: 'Compile workflow & detect connectors', status: 'pending' },
      { label: 'Check connectors on Aevatar mainnet', status: 'pending' },
      { label: 'Bind workflow to scope', status: 'pending' },
    ]
    setSyncSteps([...steps])

    const update = (idx: number, patch: Partial<SyncStep>) => {
      steps[idx] = { ...steps[idx], ...patch }
      setSyncSteps([...steps])
    }

    try {
      // Step 1: Compile and detect connectors
      update(0, { status: 'running' })
      let compiled: { workflowYaml: string; connectorJson: object[] }
      try {
        compiled = await compileWorkflow(id, token)
        const connectorNames = (compiled.connectorJson as Array<Record<string, unknown>>).map((c) => c.name as string).filter(Boolean)
        update(0, { status: 'done', error: connectorNames.length > 0 ? `Connectors: ${connectorNames.join(', ')}` : 'No connectors referenced' })
      } catch (err) {
        update(0, { status: 'error', error: err instanceof Error ? err.message : 'Compile failed' })
        return
      }

      // Step 2: Check if connectors exist on mainnet
      update(1, { status: 'running' })
      try {
        const mainnetResp = await fetch('https://aevatar-console-backend-api.aevatar.ai/api/connectors', {
          headers: { Authorization: `Bearer ${token}` },
        })
        if (!mainnetResp.ok) throw new Error(`Failed to fetch mainnet connectors: ${mainnetResp.status}`)
        const mainnetData = await mainnetResp.json() as { Connectors?: Array<{ Name: string }> }
        const mainnetNames = new Set((mainnetData.Connectors ?? []).map((c) => c.Name?.toLowerCase()))

        const requiredNames = (compiled.connectorJson as Array<Record<string, unknown>>).map((c) => c.name as string).filter(Boolean)
        const missing = requiredNames.filter((n) => !mainnetNames.has(n.toLowerCase()))

        if (missing.length > 0) {
          update(1, { status: 'error', error: `Missing connectors on mainnet: ${missing.join(', ')}. Please sync them from the Connectors page first.` })
          return
        }
        update(1, { status: 'done', error: `All ${requiredNames.length} connectors present on mainnet` })
      } catch (err) {
        update(1, { status: 'error', error: err instanceof Error ? err.message : 'Failed to check connectors' })
        return
      }

      // Step 3: Bind workflow to scope
      update(2, { status: 'running' })
      try {
        await deployWorkflow(id, token)
        update(2, { status: 'done' })
      } catch (err) {
        update(2, { status: 'error', error: err instanceof Error ? err.message : 'Binding failed' })
        return
      }

      await reloadWorkflow()
    } finally {
      setLoadingStage(null)
    }
  }, [id, getAccessToken, reloadWorkflow])

  const handleStartRun = useCallback(async () => {
    if (!workflow) return
    const token = getAccessToken()
    if (!token) return
    setLoadingStage('run'); setError(null)
    try {
      // Call runner which manages session + SSE event streaming from mainnet
      const runnerUrl = proxyUrl('sisyphus-research-runner', `/workflows/${encodeURIComponent(workflow.name)}/start`)
      const res = await fetch(runnerUrl, {
        method: 'POST',
        headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({ runMode: 'deployed', userToken: token }),
      })
      if (!res.ok) {
        const body = await res.text().catch(() => '')
        throw new Error(`${res.status}: ${body.slice(0, 200)}`)
      }
      const result = await res.json() as { sessionId?: string; runId?: string }
      setPopup({ type: 'success', title: 'Running', message: `Workflow started. Session: ${result.sessionId ?? 'unknown'}` })
      setRightTab('runs')
      setTimeout(loadRuns, 1000)
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Failed to start'
      setPopup({ type: 'error', title: 'Run Failed', message: msg })
    }
    finally { setLoadingStage(null) }
  }, [workflow, getAccessToken])

  const handleDownloadYaml = useCallback(() => {
    if (!popup?.downloadYaml) return
    const blob = new Blob([popup.downloadYaml], { type: 'text/yaml' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a'); a.href = url; a.download = popup.downloadName ?? 'workflow.yaml'
    document.body.appendChild(a); a.click(); document.body.removeChild(a); URL.revokeObjectURL(url)
  }, [popup])

  const loadRuns = useCallback(async () => {
    const token = getAccessToken()
    if (!token || !workflow) return
    setRunsLoading(true)
    try {
      const all = await fetchTriggerHistory(token)
      setRuns(all.filter((r) => r.workflowName === workflow.name))
    } catch {} finally { setRunsLoading(false) }
  }, [getAccessToken, workflow])

  useEffect(() => { if (rightTab === 'runs') loadRuns() }, [rightTab, loadRuns])

  const handleStopRun = useCallback(async (runId: string) => {
    const token = getAccessToken()
    if (!token) return
    const run = runs.find((r) => r.id === runId)
    const wfName = run?.workflowName ?? workflow?.name ?? ''
    try { await stopWorkflowRun(wfName, token); setTimeout(loadRuns, 500) } catch {}
  }, [getAccessToken, loadRuns])

  // Drag
  const handleMouseDown = useCallback(() => { dragging.current = true }, [])
  useEffect(() => {
    const move = (e: MouseEvent) => { if (!dragging.current || !containerRef.current) return; const r = containerRef.current.getBoundingClientRect(); setSplitPct(Math.max(25, Math.min(70, ((e.clientX - r.left) / r.width) * 100))) }
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
          <span className="text-sm font-semibold" style={{ color: 'var(--text-primary)' }}>{workflow?.name}</span>
          <DeploymentBadge status={workflow?.deploymentState?.status} />
          {dirty && <span className="text-[10px] px-1.5 py-0.5 rounded" style={{ background: 'rgba(252,211,77,0.15)', color: 'var(--neon-gold)' }}>unsaved</span>}
        </div>
        <div className="flex items-center gap-3">
          {error && <span className="text-[11px] max-w-[250px] truncate" style={{ color: 'var(--accent-red)' }}>{error}</span>}
          <WorkflowPipeline dirty={dirty} deploymentStatus={workflow?.deploymentState?.status}
            loadingStage={loadingStage} onSave={handleSave} onCompile={handleCompile} onDeploy={handleDeploy} onRun={handleStartRun} />
        </div>
      </div>

      {/* Split pane */}
      <div className="flex-1 flex overflow-hidden" ref={containerRef}>
        {/* Left: Structured UI */}
        <div className="flex flex-col min-w-0 overflow-auto" style={{ width: `${splitPct}%` }}>
          <div className="p-5 space-y-5">
            {/* Workflow info */}
            <div className="card p-5 space-y-3">
              <span className="text-[10px] font-semibold uppercase tracking-wider" style={{ color: 'var(--text-dimmed)' }}>Workflow Info</span>
              <label className="block">
                <span className="text-[11px] font-medium" style={{ color: 'var(--text-secondary)' }}>Name</span>
                <input className="input mt-1.5" value={form.name} onChange={(e) => updateForm({ name: e.target.value })} placeholder="sisyphus-research" />
              </label>
              <label className="block">
                <span className="text-[11px] font-medium" style={{ color: 'var(--text-secondary)' }}>Description</span>
                <textarea className="input mt-1.5 resize-none" rows={2} value={form.description}
                  onChange={(e) => updateForm({ description: e.target.value })} placeholder="What this workflow does" />
              </label>
            </div>

            {/* Roles */}
            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <span className="text-[10px] font-semibold uppercase tracking-wider flex items-center gap-1.5" style={{ color: 'var(--text-dimmed)' }}>
                  <Users size={11} /> Roles ({form.roles.length})
                </span>
                <button onClick={() => { updateForm({ roles: [...form.roles, { id: '', name: '', skillId: '', system_prompt: '' }] }); setExpandedRole(form.roles.length) }}
                  className="btn-neon-green text-[10px] gap-1 py-1 px-2"><Plus size={10} /> Add Role</button>
              </div>
              {form.roles.map((role, i) => (
                <div key={i} className="card overflow-hidden">
                  <div className="flex items-center gap-3 px-4 py-2.5">
                    <button onClick={() => setExpandedRole(expandedRole === i ? null : i)} style={{ color: 'var(--text-dimmed)' }}>
                      {expandedRole === i ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
                    </button>
                    <input className="input text-sm font-mono flex-1 py-1.5" value={role.name} placeholder="role_name"
                      onChange={(e) => { const r = [...form.roles]; r[i] = { ...role, name: e.target.value, id: role.id || e.target.value }; updateForm({ roles: r }) }} />
                    <button onClick={() => { updateForm({ roles: form.roles.filter((_, idx) => idx !== i) }); if (expandedRole === i) setExpandedRole(null) }}
                      className="icon-btn p-1" style={{ color: 'var(--neon-red)' }}><Trash2 size={12} /></button>
                  </div>
                  {expandedRole === i && (
                    <div className="px-4 pb-4 pt-2 space-y-3 animate-expand" style={{ borderTop: '1px solid var(--border-subtle)', background: 'var(--bg-base)' }}>
                      <label className="block">
                        <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Role ID</span>
                        <input className="input mt-1 font-mono" value={role.id}
                          onChange={(e) => { const r = [...form.roles]; r[i] = { ...role, id: e.target.value }; updateForm({ roles: r }) }} />
                      </label>
                      <label className="block">
                        <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>System Prompt <span className="font-normal" style={{ color: 'var(--text-dimmed)', opacity: 0.6 }}>— type / for skills & schemas</span></span>
                        <SlashCommandTextarea className="mt-1" rows={6} value={role.system_prompt}
                          onChange={(v) => { const r = [...form.roles]; r[i] = { ...role, system_prompt: v }; updateForm({ roles: r }) }} />
                      </label>
                    </div>
                  )}
                </div>
              ))}
            </div>

            {/* Steps */}
            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <span className="text-[10px] font-semibold uppercase tracking-wider flex items-center gap-1.5" style={{ color: 'var(--text-dimmed)' }}>
                  <Layers size={11} /> Steps ({form.steps.length})
                </span>
                <button onClick={() => { updateForm({ steps: [...form.steps, { id: '', type: 'llm_call', parameters: {} }] }); setExpandedStep(form.steps.length) }}
                  className="btn-neon-green text-[10px] gap-1 py-1 px-2"><Plus size={10} /> Add Step</button>
              </div>
              <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={handleDragEnd}>
                <SortableContext items={form.steps.map((s) => s.id || `step-${form.steps.indexOf(s)}`)} strategy={verticalListSortingStrategy}>
                  {form.steps.map((step, i) => (
                    <SortableStep key={step.id || `step-${i}`} id={step.id || `step-${i}`} step={step}
                      expanded={expandedStep === i} onToggleExpand={() => setExpandedStep(expandedStep === i ? null : i)}
                      roles={form.roles} onUpdate={(patch) => { const s = [...form.steps]; s[i] = { ...step, ...patch }; updateForm({ steps: s }) }}
                      onDelete={() => { updateForm({ steps: form.steps.filter((_, idx) => idx !== i) }); if (expandedStep === i) setExpandedStep(null) }}
                      onUpdateParams={(params) => { const s = [...form.steps]; s[i] = { ...step, parameters: params }; updateForm({ steps: s }) }} />
                  ))}
                </SortableContext>
              </DndContext>
            </div>
          </div>
        </div>

        {/* Divider */}
        <div onMouseDown={handleMouseDown}
          className="shrink-0 cursor-col-resize flex items-center justify-center hover:opacity-100 transition-opacity"
          style={{ width: 6, background: 'var(--border-default)', opacity: 0.6 }}>
          <div className="w-0.5 h-8 rounded" style={{ background: 'var(--text-dimmed)' }} />
        </div>

        {/* Right: Tabs */}
        <div className="min-w-0 flex flex-col" style={{ width: `${100 - splitPct}%` }}>
          <div className="flex items-center shrink-0" style={{ borderBottom: '1px solid var(--border-default)' }}>
            {([
              { id: 'yaml' as const, label: 'YAML', icon: FileCode },
              { id: 'visualizer' as const, label: 'Visualizer', icon: Eye },
              { id: 'runs' as const, label: 'Runs', icon: HistoryIcon },
            ] as const).map((t) => (
              <button key={t.id} onClick={() => setRightTab(t.id)}
                className="flex items-center gap-1.5 px-4 py-2 text-[11px] font-semibold uppercase tracking-wider transition-colors"
                style={{
                  color: rightTab === t.id ? 'var(--neon-cyan)' : 'var(--text-dimmed)',
                  borderBottom: rightTab === t.id ? '2px solid var(--neon-cyan)' : '2px solid transparent',
                }}>
                <t.icon size={12} /> {t.label}
                {t.id === 'runs' && runs.some((r) => r.status === 'running') && (
                  <span className="w-2 h-2 rounded-full animate-pulse" style={{ background: 'var(--neon-gold)' }} />
                )}
              </button>
            ))}
          </div>

          <div className="flex-1 overflow-hidden" style={{ minHeight: 0 }}>
            {rightTab === 'yaml' && (
              <div style={{ height: '100%', overflow: 'auto' }}>
                <CodeMirror value={yamlContent} onChange={handleYamlChange}
                  extensions={[StreamLanguage.define(yamlMode), refHighlightPlugin, refHighlightTheme]}
                  theme="dark" height="100%" style={{ height: '100%' }}
                  basicSetup={{ lineNumbers: true, foldGutter: true, bracketMatching: true }} />
              </div>
            )}
            {rightTab === 'visualizer' && (
              <WorkflowVisualizer yamlContent={yamlContent} onStepClick={() => {}} connectors={connectorDefs} />
            )}
            {rightTab === 'runs' && (
              <div className="h-full overflow-auto p-4">
                <div className="flex items-center justify-between mb-3">
                  <span className="text-xs font-semibold" style={{ color: 'var(--text-primary)' }}>Run History</span>
                  <button onClick={loadRuns} className="icon-btn p-1"><RefreshCw size={13} className={runsLoading ? 'animate-spin' : ''} /></button>
                </div>
                {runsLoading && runs.length === 0 && <div className="flex justify-center py-8"><Loader2 size={14} className="animate-spin" style={{ color: 'var(--text-dimmed)' }} /></div>}
                {!runsLoading && runs.length === 0 && <div className="text-center py-8"><HistoryIcon size={20} style={{ color: 'var(--text-dimmed)' }} className="mx-auto mb-2" /><p className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>No runs yet</p></div>}
                <div className="space-y-1.5">
                  {runs.map((run) => (
                    <div key={run.id} className="card card-hover flex items-center gap-2 px-3 py-2">
                      <StatusIcon status={run.status} />
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center gap-2">
                          <span className="text-[11px] font-mono truncate" style={{ color: 'var(--text-primary)' }}>{run.id.slice(0, 8)}</span>
                          <span className="text-[9px] font-medium uppercase px-1 py-0.5 rounded" style={{ color: statusColor(run.status), border: `1px solid ${statusColor(run.status)}30` }}>{run.status}</span>
                        </div>
                        <div className="text-[9px] flex items-center gap-1.5 mt-0.5" style={{ color: 'var(--text-dimmed)' }}>
                          <span>{run.triggeredBy}</span><Clock size={8} /><span>{new Date(run.triggeredAt).toLocaleString()}</span>
                          {run.durationMs != null && <span>{(run.durationMs / 1000).toFixed(1)}s</span>}
                        </div>
                      </div>
                      <div className="flex items-center gap-1 shrink-0">
                        {run.status === 'running' && <button onClick={() => handleStopRun(run.id)} className="icon-btn p-1" style={{ color: 'var(--accent-red)' }}><Square size={11} /></button>}
                        <Link to={`/workflows/history/${run.id}`} className="icon-btn p-1"><Eye size={11} /></Link>
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Sync progress popup */}
      {syncSteps && (
        <div className="fixed inset-0 z-50 flex items-center justify-center" style={{ background: 'rgba(0,0,0,0.5)', backdropFilter: 'blur(4px)' }}>
          <div className="rounded-lg overflow-hidden animate-scale-in" style={{ background: 'var(--bg-surface)', border: '1px solid var(--border-default)', boxShadow: 'var(--shadow-lg)', minWidth: 400, maxWidth: 520 }}>
            <div className="px-5 py-3" style={{ borderBottom: '1px solid var(--border-subtle)' }}>
              <span className="text-sm font-semibold" style={{ color: 'var(--text-primary)' }}>Sync to Aevatar</span>
            </div>
            <div className="px-5 py-4 space-y-3">
              {syncSteps.map((step, i) => (
                <div key={i} className="flex items-start gap-3">
                  <div className="shrink-0 mt-0.5">
                    {step.status === 'pending' && <div className="w-4 h-4 rounded-full" style={{ border: '2px solid var(--border-default)' }} />}
                    {step.status === 'running' && <Loader2 size={16} className="animate-spin" style={{ color: 'var(--neon-cyan)' }} />}
                    {step.status === 'done' && <CheckCircle2 size={16} style={{ color: 'var(--neon-green)' }} />}
                    {step.status === 'error' && <AlertCircle size={16} style={{ color: 'var(--neon-red)' }} />}
                  </div>
                  <div className="flex-1 min-w-0">
                    <div className="text-xs font-medium" style={{ color: step.status === 'error' ? 'var(--neon-red)' : step.status === 'done' ? 'var(--neon-green)' : 'var(--text-secondary)' }}>
                      {step.label}
                    </div>
                    {step.error && (
                      <div className="text-[10px] mt-1 break-all" style={{ color: step.status === 'error' ? 'var(--neon-red)' : 'var(--text-dimmed)', maxHeight: 80, overflow: 'auto' }}>
                        {step.error}
                      </div>
                    )}
                  </div>
                </div>
              ))}
            </div>
            <div className="flex items-center justify-end px-5 py-3" style={{ borderTop: '1px solid var(--border-subtle)' }}>
              {(syncSteps.every((s) => s.status === 'done') || syncSteps.some((s) => s.status === 'error')) && (
                <button onClick={() => setSyncSteps(null)} className="btn-secondary text-xs py-1.5 px-3">Close</button>
              )}
            </div>
          </div>
        </div>
      )}

      {/* Response popup */}
      {popup && (
        <div className="fixed inset-0 z-50 flex items-center justify-center" style={{ background: 'rgba(0,0,0,0.5)', backdropFilter: 'blur(4px)' }}>
          <div className="rounded-lg overflow-hidden animate-scale-in" style={{ background: 'var(--bg-surface)', border: '1px solid var(--border-default)', boxShadow: 'var(--shadow-lg)', minWidth: 320, maxWidth: 560 }}>
            <div className="flex items-center gap-2 px-5 py-3" style={{ borderBottom: '1px solid var(--border-subtle)' }}>
              <div className="w-2 h-2 rounded-full" style={{ background: popup.type === 'success' ? 'var(--neon-green)' : 'var(--neon-red)', boxShadow: popup.type === 'success' ? 'var(--glow-green)' : '0 0 8px rgba(252,165,165,0.5)' }} />
              <span className="text-sm font-semibold" style={{ color: popup.type === 'success' ? 'var(--neon-green)' : 'var(--neon-red)' }}>{popup.title}</span>
            </div>
            <div className="px-5 py-4">
              <p className="text-xs break-all" style={{ color: 'var(--text-secondary)', maxHeight: 200, overflow: 'auto' }}>{popup.message}</p>
            </div>
            <div className="flex items-center justify-end gap-2 px-5 py-3" style={{ borderTop: '1px solid var(--border-subtle)' }}>
              {popup.downloadYaml && (
                <button onClick={handleDownloadYaml} className="btn-neon text-xs py-1.5 px-3">
                  Download YAML
                </button>
              )}
              <button onClick={() => setPopup(null)} className="btn-secondary text-xs py-1.5 px-3">
                Close
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
