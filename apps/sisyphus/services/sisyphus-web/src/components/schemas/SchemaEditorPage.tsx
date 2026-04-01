import { useState, useEffect, useCallback, useRef, useMemo } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { ArrowLeft, Save, Loader2, Plus, Trash2, Lock, ChevronDown, ChevronRight } from 'lucide-react'
import CodeMirror from '@uiw/react-codemirror'
import { json } from '@codemirror/lang-json'
import { useAuth } from '../../auth/useAuth'
import { fetchSchema, createSchema, updateSchema } from '../../api/schemas-api'
import Select from '../shared/Select'

// --- Types ---

interface SchemaField {
  name: string
  type: string
  optional: boolean
  defaultValue?: string
  description?: string
  format?: string
  minLength?: number
  maxLength?: number
  pattern?: string
  minimum?: number
  maximum?: number
  exclusiveMinimum?: number
  exclusiveMaximum?: number
  multipleOf?: number
  itemsType?: string
  minItems?: number
  maxItems?: number
  uniqueItems?: boolean
  enumValues?: string
  constValue?: string
}

interface SchemaFormState {
  name: string
  description: string
  entityType: 'node' | 'edge'
  fields: SchemaField[]
}

const NODE_META_FIELDS = [
  { name: 'id', type: 'string', format: 'uuid', description: 'Unique node identifier' },
  { name: 'graphId', type: 'string', description: 'Graph this node belongs to' },
  { name: 'type', type: 'string', description: 'Node type (= schema name)' },
  { name: 'createdBy', type: 'string', description: 'Creator identity' },
  { name: 'createdAt', type: 'string', format: 'date-time', description: 'Creation timestamp' },
  { name: 'updatedBy', type: 'string', description: 'Last updater identity' },
  { name: 'updatedAt', type: 'string', format: 'date-time', description: 'Last update timestamp' },
]

const EDGE_META_FIELDS = [
  { name: 'id', type: 'string', format: 'uuid', description: 'Unique edge identifier' },
  { name: 'graphId', type: 'string', description: 'Graph this edge belongs to' },
  { name: 'type', type: 'string', description: 'Edge type (e.g. proves, references)' },
  { name: 'source', type: 'string', description: 'Source node ID' },
  { name: 'target', type: 'string', description: 'Target node ID' },
  { name: 'createdBy', type: 'string', description: 'Creator identity' },
  { name: 'createdAt', type: 'string', format: 'date-time', description: 'Creation timestamp' },
  { name: 'updatedBy', type: 'string', description: 'Last updater identity' },
  { name: 'updatedAt', type: 'string', format: 'date-time', description: 'Last update timestamp' },
]

const RESERVED_NAMES = new Set(
  ['id', 'graphid', 'type', 'createdby', 'createdat', 'updatedby', 'updatedat',
   'created_by', 'created_at', 'updated_by', 'updated_at', 'graph_id',
   'source', 'target'],
)

const FIELD_TYPES = ['string', 'number', 'integer', 'boolean', 'array', 'object']
const STRING_FORMATS = ['', 'date-time', 'date', 'time', 'email', 'hostname', 'ipv4', 'ipv6', 'uri', 'uri-reference', 'uuid', 'regex']

// --- Converters ---

function fieldToJsonSchemaProp(f: SchemaField): Record<string, unknown> {
  const prop: Record<string, unknown> = { type: f.type }
  if (f.description) prop.description = f.description
  if (f.defaultValue !== undefined && f.defaultValue !== '') {
    try { prop.default = JSON.parse(f.defaultValue) } catch { prop.default = f.defaultValue }
  }
  if (f.enumValues) { try { prop.enum = JSON.parse(f.enumValues) } catch { /* skip */ } }
  if (f.constValue !== undefined && f.constValue !== '') {
    try { prop.const = JSON.parse(f.constValue) } catch { prop.const = f.constValue }
  }
  if (f.type === 'string') {
    if (f.format) prop.format = f.format
    if (f.minLength !== undefined && f.minLength > 0) prop.minLength = f.minLength
    if (f.maxLength !== undefined && f.maxLength > 0) prop.maxLength = f.maxLength
    if (f.pattern) prop.pattern = f.pattern
  }
  if (f.type === 'number' || f.type === 'integer') {
    if (f.minimum !== undefined) prop.minimum = f.minimum
    if (f.maximum !== undefined) prop.maximum = f.maximum
    if (f.exclusiveMinimum !== undefined) prop.exclusiveMinimum = f.exclusiveMinimum
    if (f.exclusiveMaximum !== undefined) prop.exclusiveMaximum = f.exclusiveMaximum
    if (f.multipleOf !== undefined && f.multipleOf > 0) prop.multipleOf = f.multipleOf
  }
  if (f.type === 'array') {
    if (f.itemsType) prop.items = { type: f.itemsType }
    if (f.minItems !== undefined && f.minItems > 0) prop.minItems = f.minItems
    if (f.maxItems !== undefined && f.maxItems > 0) prop.maxItems = f.maxItems
    if (f.uniqueItems) prop.uniqueItems = true
  }
  return prop
}

function fieldsToJsonSchema(fields: SchemaField[]): Record<string, unknown> {
  const properties: Record<string, Record<string, unknown>> = {}
  const required: string[] = []
  for (const f of fields) {
    if (!f.name.trim()) continue
    properties[f.name] = fieldToJsonSchemaProp(f)
    if (!f.optional) required.push(f.name)
  }
  const schema: Record<string, unknown> = { type: 'object', properties }
  if (required.length > 0) schema.required = required
  return schema
}

function jsonSchemaPropToField(name: string, prop: Record<string, unknown>, isRequired: boolean): SchemaField {
  const items = prop.items as Record<string, unknown> | undefined
  return {
    name, type: (prop.type as string) ?? 'string', optional: !isRequired,
    defaultValue: prop.default !== undefined ? JSON.stringify(prop.default) : '',
    description: (prop.description as string) ?? '',
    format: (prop.format as string) ?? '',
    minLength: prop.minLength as number | undefined, maxLength: prop.maxLength as number | undefined,
    pattern: (prop.pattern as string) ?? '',
    minimum: prop.minimum as number | undefined, maximum: prop.maximum as number | undefined,
    exclusiveMinimum: prop.exclusiveMinimum as number | undefined, exclusiveMaximum: prop.exclusiveMaximum as number | undefined,
    multipleOf: prop.multipleOf as number | undefined,
    itemsType: (items?.type as string) ?? '', minItems: prop.minItems as number | undefined,
    maxItems: prop.maxItems as number | undefined, uniqueItems: (prop.uniqueItems as boolean) ?? false,
    enumValues: prop.enum ? JSON.stringify(prop.enum) : '', constValue: prop.const !== undefined ? JSON.stringify(prop.const) : '',
  }
}

function jsonSchemaToFields(schema: Record<string, unknown>): SchemaField[] {
  const props = (schema.properties ?? {}) as Record<string, Record<string, unknown>>
  const required = new Set((schema.required as string[]) ?? [])
  return Object.entries(props)
    .filter(([name]) => !RESERVED_NAMES.has(name.toLowerCase()))
    .map(([name, prop]) => jsonSchemaPropToField(name, prop, required.has(name)))
}

function buildFullPreview(schemaName: string, entityType: 'node' | 'edge', userSchema: Record<string, unknown>): Record<string, unknown> {
  const metaFields = entityType === 'edge' ? EDGE_META_FIELDS : NODE_META_FIELDS
  const metaProps: Record<string, unknown> = {}
  const metaRequired: string[] = []
  for (const m of metaFields) {
    const p: Record<string, unknown> = { type: m.type, description: m.description }
    if (m.format) p.format = m.format
    metaProps[m.name] = p
    metaRequired.push(m.name)
  }
  return {
    $schema: 'http://json-schema.org/draft-07/schema#',
    title: schemaName || 'UntitledSchema',
    type: 'object',
    properties: { ...metaProps, ...(userSchema.properties ?? {}) as Record<string, unknown> },
    required: [...metaRequired, ...((userSchema.required as string[]) ?? [])],
  }
}

// --- Helpers ---

function NumInput({ value, onChange, placeholder }: { value: number | undefined; onChange: (v: number | undefined) => void; placeholder: string }) {
  return (
    <input type="number" className="input text-xs w-full" value={value ?? ''}
      onChange={(e) => onChange(e.target.value === '' ? undefined : Number(e.target.value))} placeholder={placeholder} />
  )
}

function SectionLabel({ children }: { children: React.ReactNode }) {
  return <span className="text-[10px] font-semibold uppercase tracking-wider" style={{ color: 'var(--text-dimmed)' }}>{children}</span>
}

// --- Component ---

export default function SchemaEditorPage() {
  const { id } = useParams<{ id: string }>()
  const isNew = id === 'new'
  const navigate = useNavigate()
  const { getAccessToken } = useAuth()

  const [form, setForm] = useState<SchemaFormState>({
    name: '', description: '', entityType: 'node',
    fields: [{ name: 'abstract', type: 'string', optional: false }],
  })
  const [jsonContent, setJsonContent] = useState('')
  const [loading, setLoading] = useState(!isNew)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [jsonError, setJsonError] = useState<string | null>(null)
  const [expandedField, setExpandedField] = useState<number | null>(null)
  const [showSystemFields, setShowSystemFields] = useState(!isNew) // auto-expand when editing existing
  const [rightTab, setRightTab] = useState<'editor' | 'preview'>('editor')

  const lastEditSource = useRef<'ui' | 'json'>('ui')
  const [splitPct, setSplitPct] = useState(55)
  const containerRef = useRef<HTMLDivElement>(null)
  const dragging = useRef(false)

  const metaFields = form.entityType === 'edge' ? EDGE_META_FIELDS : NODE_META_FIELDS

  const fullPreview = useMemo(() => {
    try {
      const userSchema = JSON.parse(jsonContent || '{}')
      return JSON.stringify(buildFullPreview(form.name, form.entityType, userSchema), null, 2)
    } catch { return '// Invalid JSON schema above' }
  }, [jsonContent, form.name, form.entityType])

  useEffect(() => {
    if (isNew || !id) return
    const token = getAccessToken()
    if (!token) return
    setLoading(true)
    fetchSchema(id, token)
      .then((data) => {
        setForm({ name: data.name, description: data.description, entityType: data.entityType ?? 'node', fields: jsonSchemaToFields(data.jsonSchema) })
        setJsonContent(JSON.stringify(data.jsonSchema, null, 2))
      })
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load schema'))
      .finally(() => setLoading(false))
  }, [id, isNew, getAccessToken])

  useEffect(() => {
    if (lastEditSource.current !== 'ui') return
    setJsonContent(JSON.stringify(fieldsToJsonSchema(form.fields), null, 2))
    setJsonError(null)
  }, [form.fields])

  const handleJsonChange = useCallback((value: string) => {
    lastEditSource.current = 'json'
    setJsonContent(value)
    try { const p = JSON.parse(value); setJsonError(null); setForm((prev) => ({ ...prev, fields: jsonSchemaToFields(p) })) }
    catch (e) { setJsonError(e instanceof Error ? e.message : 'Invalid JSON') }
  }, [])

  const updateField = useCallback((i: number, patch: Partial<SchemaField>) => {
    lastEditSource.current = 'ui'
    setForm((prev) => ({ ...prev, fields: prev.fields.map((f, idx) => (idx === i ? { ...f, ...patch } : f)) }))
  }, [])

  const addField = useCallback(() => {
    lastEditSource.current = 'ui'
    setForm((prev) => ({ ...prev, fields: [...prev.fields, { name: '', type: 'string', optional: true }] }))
    setExpandedField(form.fields.length)
  }, [form.fields.length])

  const removeField = useCallback((i: number) => {
    lastEditSource.current = 'ui'
    setForm((prev) => ({ ...prev, fields: prev.fields.filter((_, idx) => idx !== i) }))
    if (expandedField === i) setExpandedField(null)
    else if (expandedField !== null && expandedField > i) setExpandedField(expandedField - 1)
  }, [expandedField])

  const handleSave = useCallback(async () => {
    const token = getAccessToken()
    if (!token || !form.name.trim()) { setError('Name is required'); return }
    let parsedSchema: Record<string, unknown>
    try { parsedSchema = JSON.parse(jsonContent) } catch { setError('Invalid JSON schema'); return }
    setSaving(true); setError(null)
    try {
      const payload = { name: form.name.trim(), description: form.description.trim(), entityType: form.entityType, nodeType: form.name.trim(), applicableTypes: [form.name.trim()], jsonSchema: parsedSchema }
      if (isNew) await createSchema(payload, token); else await updateSchema(id!, payload, token)
      navigate('/schemas')
    } catch (err) { setError(err instanceof Error ? err.message : 'Save failed') }
    finally { setSaving(false) }
  }, [form, jsonContent, isNew, id, getAccessToken, navigate])

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
          <button onClick={() => navigate('/schemas')} className="icon-btn"><ArrowLeft size={16} /></button>
          <span className="text-sm font-semibold" style={{ color: 'var(--text-primary)' }}>
            {isNew ? 'New Schema' : form.name || 'Edit Schema'}
          </span>
          <span className="badge badge-cyan text-[10px]">{form.entityType}</span>
        </div>
        <div className="flex items-center gap-2">
          {error && <span className="text-[11px] max-w-[300px] truncate" style={{ color: 'var(--accent-red)' }}>{error}</span>}
          <button onClick={handleSave} disabled={saving || !form.name.trim() || !!jsonError}
            className="btn-neon-green text-xs gap-1.5 py-1.5 px-4 disabled:opacity-50">
            {saving ? <Loader2 size={14} className="animate-spin" /> : <Save size={14} />}
            {isNew ? 'Create' : 'Save'}
          </button>
        </div>
      </div>

      {/* Split pane */}
      <div className="flex-1 flex overflow-hidden" ref={containerRef}>
        {/* Left: UI Editor */}
        <div className="flex flex-col min-w-0 overflow-auto" style={{ width: `${splitPct}%` }}>
          <div className="p-5 space-y-6">

            {/* Schema info — full width cards */}
            <div className="card p-5 space-y-4">
              <SectionLabel>Schema Info</SectionLabel>
              <div className="grid grid-cols-[1fr_120px] gap-3">
                <label className="block">
                  <span className="text-[11px] font-medium" style={{ color: 'var(--text-secondary)' }}>
                    Name <span className="font-normal" style={{ color: 'var(--text-dimmed)' }}>({form.entityType} type in chrono-graph)</span>
                  </span>
                  <input className="input mt-1.5" value={form.name}
                    onChange={(e) => setForm((p) => ({ ...p, name: e.target.value }))}
                    placeholder={form.entityType === 'node' ? 'e.g. TheoremNode' : 'e.g. proves'} />
                </label>
                <label className="block">
                  <span className="text-[11px] font-medium" style={{ color: 'var(--text-secondary)' }}>Entity</span>
                  <Select className="mt-1.5" value={form.entityType}
                    options={[{ value: 'node', label: 'Node' }, { value: 'edge', label: 'Edge' }]}
                    onChange={(v) => setForm((p) => ({ ...p, entityType: v as 'node' | 'edge' }))} />
                </label>
              </div>
              <label className="block">
                <span className="text-[11px] font-medium" style={{ color: 'var(--text-secondary)' }}>Description</span>
                <input className="input mt-1.5" value={form.description}
                  onChange={(e) => setForm((p) => ({ ...p, description: e.target.value }))} placeholder="What this schema defines" />
              </label>
            </div>

            {/* System fields — collapsible */}
            <div className="card overflow-hidden">
              <button onClick={() => setShowSystemFields(!showSystemFields)}
                className="w-full flex items-center justify-between px-5 py-3 text-left transition-colors hover:bg-[var(--bg-elevated)]">
                <span className="flex items-center gap-2">
                  <Lock size={12} style={{ color: 'var(--text-dimmed)' }} />
                  <span className="text-[11px] font-semibold uppercase tracking-wider" style={{ color: 'var(--text-dimmed)' }}>
                    System Fields ({metaFields.length})
                  </span>
                </span>
                {showSystemFields ? <ChevronDown size={14} style={{ color: 'var(--text-dimmed)' }} /> : <ChevronRight size={14} style={{ color: 'var(--text-dimmed)' }} />}
              </button>
              {showSystemFields && (
                <div className="px-5 pb-4 space-y-1.5 animate-expand" style={{ borderTop: '1px solid var(--border-subtle)' }}>
                  {metaFields.map((f) => (
                    <div key={f.name} className="flex items-center gap-3 py-1.5 px-3 rounded" style={{ background: 'var(--bg-base)', opacity: 0.7 }}>
                      <Lock size={9} style={{ color: 'var(--text-dimmed)' }} />
                      <span className="font-mono text-xs w-28 shrink-0" style={{ color: 'var(--text-secondary)' }}>{f.name}</span>
                      <span className="text-[11px] w-32 shrink-0" style={{ color: 'var(--text-dimmed)' }}>{f.type}{f.format ? ` (${f.format})` : ''}</span>
                      <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>{f.description}</span>
                    </div>
                  ))}
                </div>
              )}
            </div>

            {/* Custom fields */}
            <div className="space-y-3">
              <div className="flex items-center justify-between">
                <SectionLabel>Custom Properties ({form.fields.length})</SectionLabel>
                <button onClick={addField} className="btn-neon-green text-[11px] gap-1.5 py-1.5 px-3">
                  <Plus size={12} /> Add Field
                </button>
              </div>

              {form.fields.length === 0 && (
                <div className="card p-8 text-center">
                  <p className="text-xs" style={{ color: 'var(--text-dimmed)' }}>No custom fields defined. Click "Add Field" to get started.</p>
                </div>
              )}

              <div className="space-y-2">
                {form.fields.map((field, i) => {
                  const isExpanded = expandedField === i
                  return (
                    <div key={i} className="card overflow-hidden transition-all" style={isExpanded ? { borderColor: 'var(--border-default)' } : {}}>
                      {/* Field header row */}
                      <div className="flex items-center gap-3 px-4 py-3">
                        <button onClick={() => setExpandedField(isExpanded ? null : i)} className="shrink-0"
                          style={{ color: 'var(--text-dimmed)' }}>
                          {isExpanded ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
                        </button>

                        <input className="input text-sm font-mono flex-1 min-w-0 py-2" value={field.name}
                          onChange={(e) => updateField(i, { name: e.target.value })} placeholder="field_name" />

                        <Select className="w-28 shrink-0" value={field.type} options={FIELD_TYPES}
                          onChange={(v) => updateField(i, { type: v })} />

                        <label className="flex items-center gap-1.5 shrink-0 cursor-pointer select-none"
                          title={field.optional ? 'Optional' : 'Required'}>
                          <input type="checkbox" checked={!field.optional}
                            onChange={(e) => updateField(i, { optional: !e.target.checked })}
                            className="accent-[var(--neon-cyan)]" />
                          <span className="text-[10px] font-medium" style={{ color: field.optional ? 'var(--text-dimmed)' : 'var(--neon-cyan)' }}>
                            {field.optional ? 'optional' : 'required'}
                          </span>
                        </label>

                        <button onClick={() => removeField(i)} className="icon-btn shrink-0 p-1.5" style={{ color: 'var(--neon-red)' }}>
                          <Trash2 size={13} />
                        </button>
                      </div>

                      {/* Expanded details */}
                      {isExpanded && (
                        <div className="px-4 pb-4 pt-2 space-y-4 animate-expand" style={{ borderTop: '1px solid var(--border-subtle)', background: 'var(--bg-base)' }}>
                          {/* Common */}
                          <div className="grid grid-cols-2 gap-3">
                            <label className="block">
                              <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Description</span>
                              <input className="input mt-1" value={field.description ?? ''}
                                onChange={(e) => updateField(i, { description: e.target.value })} placeholder="Field description" />
                            </label>
                            <label className="block">
                              <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Default Value</span>
                              <input className="input mt-1 font-mono" value={field.defaultValue ?? ''}
                                onChange={(e) => updateField(i, { defaultValue: e.target.value })} placeholder='"en" or 0 or true' />
                            </label>
                          </div>
                          <div className="grid grid-cols-2 gap-3">
                            <label className="block">
                              <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Enum (JSON array)</span>
                              <input className="input mt-1 font-mono" value={field.enumValues ?? ''}
                                onChange={(e) => updateField(i, { enumValues: e.target.value })} placeholder='["a","b","c"]' />
                            </label>
                            <label className="block">
                              <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Const</span>
                              <input className="input mt-1 font-mono" value={field.constValue ?? ''}
                                onChange={(e) => updateField(i, { constValue: e.target.value })} placeholder='"fixed_value"' />
                            </label>
                          </div>

                          {/* String */}
                          {field.type === 'string' && (
                            <div>
                              <SectionLabel>String Constraints</SectionLabel>
                              <div className="grid grid-cols-4 gap-3 mt-2">
                                <label className="block">
                                  <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Format</span>
                                  <Select className="mt-1" value={field.format ?? ''} options={STRING_FORMATS}
                                    onChange={(v) => updateField(i, { format: v })} />
                                </label>
                                <label className="block">
                                  <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Min Length</span>
                                  <NumInput value={field.minLength} onChange={(v) => updateField(i, { minLength: v })} placeholder="0" />
                                </label>
                                <label className="block">
                                  <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Max Length</span>
                                  <NumInput value={field.maxLength} onChange={(v) => updateField(i, { maxLength: v })} placeholder="—" />
                                </label>
                                <label className="block">
                                  <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Pattern</span>
                                  <input className="input mt-1 font-mono" value={field.pattern ?? ''}
                                    onChange={(e) => updateField(i, { pattern: e.target.value })} placeholder="^[a-z]+$" />
                                </label>
                              </div>
                            </div>
                          )}

                          {/* Number / Integer */}
                          {(field.type === 'number' || field.type === 'integer') && (
                            <div>
                              <SectionLabel>Numeric Constraints</SectionLabel>
                              <div className="grid grid-cols-5 gap-3 mt-2">
                                <label className="block">
                                  <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Min</span>
                                  <NumInput value={field.minimum} onChange={(v) => updateField(i, { minimum: v })} placeholder="—" />
                                </label>
                                <label className="block">
                                  <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Max</span>
                                  <NumInput value={field.maximum} onChange={(v) => updateField(i, { maximum: v })} placeholder="—" />
                                </label>
                                <label className="block">
                                  <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Excl Min</span>
                                  <NumInput value={field.exclusiveMinimum} onChange={(v) => updateField(i, { exclusiveMinimum: v })} placeholder="—" />
                                </label>
                                <label className="block">
                                  <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Excl Max</span>
                                  <NumInput value={field.exclusiveMaximum} onChange={(v) => updateField(i, { exclusiveMaximum: v })} placeholder="—" />
                                </label>
                                <label className="block">
                                  <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Multiple Of</span>
                                  <NumInput value={field.multipleOf} onChange={(v) => updateField(i, { multipleOf: v })} placeholder="—" />
                                </label>
                              </div>
                            </div>
                          )}

                          {/* Array */}
                          {field.type === 'array' && (
                            <div>
                              <SectionLabel>Array Constraints</SectionLabel>
                              <div className="grid grid-cols-4 gap-3 mt-2">
                                <label className="block">
                                  <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Items Type</span>
                                  <Select className="mt-1" value={field.itemsType ?? ''}
                                    options={['', ...FIELD_TYPES]}
                                    onChange={(v) => updateField(i, { itemsType: v })} />
                                </label>
                                <label className="block">
                                  <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Min Items</span>
                                  <NumInput value={field.minItems} onChange={(v) => updateField(i, { minItems: v })} placeholder="0" />
                                </label>
                                <label className="block">
                                  <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Max Items</span>
                                  <NumInput value={field.maxItems} onChange={(v) => updateField(i, { maxItems: v })} placeholder="—" />
                                </label>
                                <label className="flex items-center gap-2 self-end pb-3">
                                  <input type="checkbox" checked={field.uniqueItems ?? false}
                                    onChange={(e) => updateField(i, { uniqueItems: e.target.checked })} className="accent-[var(--neon-cyan)]" />
                                  <span className="text-[11px]" style={{ color: 'var(--text-dimmed)' }}>Unique</span>
                                </label>
                              </div>
                            </div>
                          )}
                        </div>
                      )}
                    </div>
                  )
                })}
              </div>
            </div>
          </div>
        </div>

        {/* Divider */}
        <div onMouseDown={handleMouseDown}
          className="shrink-0 cursor-col-resize flex items-center justify-center hover:opacity-100 transition-opacity"
          style={{ width: 6, background: 'var(--border-default)', opacity: 0.6 }}>
          <div className="w-0.5 h-8 rounded" style={{ background: 'var(--text-dimmed)' }} />
        </div>

        {/* Right: Tabbed JSON Editor / Full Preview */}
        <div className="min-w-0 flex flex-col" style={{ width: `${100 - splitPct}%` }}>
          {/* Tabs */}
          <div className="flex items-center shrink-0" style={{ borderBottom: '1px solid var(--border-default)' }}>
            <button onClick={() => setRightTab('editor')}
              className="flex items-center gap-1.5 px-4 py-2 text-[11px] font-semibold uppercase tracking-wider transition-colors"
              style={{
                color: rightTab === 'editor' ? 'var(--neon-cyan)' : 'var(--text-dimmed)',
                borderBottom: rightTab === 'editor' ? '2px solid var(--neon-cyan)' : '2px solid transparent',
              }}>
              JSON Schema
            </button>
            <button onClick={() => setRightTab('preview')}
              className="flex items-center gap-1.5 px-4 py-2 text-[11px] font-semibold uppercase tracking-wider transition-colors"
              style={{
                color: rightTab === 'preview' ? 'var(--neon-cyan)' : 'var(--text-dimmed)',
                borderBottom: rightTab === 'preview' ? '2px solid var(--neon-cyan)' : '2px solid transparent',
              }}>
              <Lock size={10} />
              Full Preview
            </button>
            {rightTab === 'editor' && jsonError && (
              <span className="ml-auto mr-3 text-[10px]" style={{ color: 'var(--accent-red)' }}>{jsonError}</span>
            )}
          </div>

          {/* Tab content */}
          <div className="flex-1 min-h-0">
            {rightTab === 'editor' ? (
              <CodeMirror value={jsonContent} onChange={handleJsonChange} extensions={[json()]} theme="dark" height="100%"
                basicSetup={{ lineNumbers: true, foldGutter: true, bracketMatching: true, closeBrackets: true }} />
            ) : (
              <CodeMirror value={fullPreview} extensions={[json()]} theme="dark" height="100%" readOnly
                basicSetup={{ lineNumbers: true, foldGutter: true }} />
            )}
          </div>
        </div>
      </div>
    </div>
  )
}
