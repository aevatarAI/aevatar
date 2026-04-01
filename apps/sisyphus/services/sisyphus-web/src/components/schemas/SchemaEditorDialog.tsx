import { useState, useEffect, useCallback } from 'react'
import { X, Save, Loader2 } from 'lucide-react'
import CodeMirror from '@uiw/react-codemirror'
import { json } from '@codemirror/lang-json'
import { useAuth } from '../../auth/useAuth'
import { fetchSchema, createSchema, updateSchema } from '../../api/schemas-api'
import type { SchemaDefinition } from '../../types/schema'

interface SchemaEditorDialogProps {
  schemaId: string | null
  onClose: () => void
}

export default function SchemaEditorDialog({ schemaId, onClose }: SchemaEditorDialogProps) {
  const { getAccessToken } = useAuth()
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [entityType, setEntityType] = useState<'node' | 'edge'>('node')
  const [nodeType, setNodeType] = useState('')
  const [applicableTypes, setApplicableTypes] = useState('')
  const [schemaJson, setSchemaJson] = useState(JSON.stringify({
    type: "object",
    required: ["abstract", "body"],
    properties: {
      abstract: { type: "string", minLength: 1 },
      body: { type: "string", minLength: 1 }
    }
  }, null, 2))
  const [loading, setLoading] = useState(!!schemaId)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [jsonError, setJsonError] = useState<string | null>(null)

  useEffect(() => {
    if (!schemaId) return
    const token = getAccessToken()
    if (!token) return

    setLoading(true)
    fetchSchema(schemaId, token)
      .then((data: SchemaDefinition) => {
        setName(data.name)
        setDescription(data.description)
        setEntityType(data.entityType)
        setNodeType(data.nodeType ?? '')
        setApplicableTypes(data.applicableTypes.join(', '))
        setSchemaJson(JSON.stringify(data.jsonSchema, null, 2))
      })
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load schema'))
      .finally(() => setLoading(false))
  }, [schemaId, getAccessToken])

  const validateJson = useCallback((value: string) => {
    try {
      JSON.parse(value)
      setJsonError(null)
    } catch (e) {
      setJsonError(e instanceof Error ? e.message : 'Invalid JSON')
    }
  }, [])

  const handleSchemaChange = useCallback(
    (value: string) => {
      setSchemaJson(value)
      validateJson(value)
    },
    [validateJson],
  )

  const handleSave = useCallback(async () => {
    const token = getAccessToken()
    if (!token) return

    // Validate JSON
    let parsedSchema: Record<string, unknown>
    try {
      parsedSchema = JSON.parse(schemaJson)
    } catch {
      setError('Invalid JSON in schema body')
      return
    }

    if (!name.trim()) {
      setError('Name is required')
      return
    }

    setSaving(true)
    setError(null)

    try {
      const types = applicableTypes.split(',').map((t) => t.trim()).filter(Boolean)

      if (schemaId) {
        await updateSchema(schemaId, {
          name: name.trim(),
          description: description.trim(),
          entityType,
          nodeType: nodeType.trim(),
          applicableTypes: types,
          jsonSchema: parsedSchema,
        }, token)
      } else {
        await createSchema({
          name: name.trim(),
          description: description.trim(),
          entityType,
          nodeType: nodeType.trim(),
          applicableTypes: types,
          jsonSchema: parsedSchema,
        }, token)
      }
      onClose()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Save failed')
    } finally {
      setSaving(false)
    }
  }, [schemaId, name, description, entityType, nodeType, applicableTypes, schemaJson, getAccessToken, onClose])

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center" style={{ background: 'rgba(0,0,0,0.6)', backdropFilter: 'blur(4px)' }}>
      <div
        className="w-[700px] max-h-[85vh] flex flex-col rounded-lg overflow-hidden animate-scale-in"
        style={{ background: 'var(--bg-surface)', border: '1px solid var(--border-default)' }}
      >
        {/* Header */}
        <div className="flex items-center justify-between px-5 py-3 shrink-0" style={{ borderBottom: '1px solid var(--border-default)' }}>
          <span className="text-sm font-semibold" style={{ color: 'var(--text-primary)' }}>
            {schemaId ? 'Edit Schema' : 'New Schema'}
          </span>
          <button onClick={onClose} className="icon-btn"><X size={16} /></button>
        </div>

        {loading ? (
          <div className="flex items-center justify-center py-12">
            <Loader2 size={20} className="animate-spin" style={{ color: 'var(--text-dimmed)' }} />
          </div>
        ) : (
          <>
            {/* Form */}
            <div className="flex-1 overflow-auto p-5 space-y-4">
              {error && (
                <div className="px-3 py-2 rounded text-xs" style={{ background: 'rgba(252,165,165,0.08)', border: '1px solid rgba(252,165,165,0.2)', color: 'var(--accent-red)' }}>
                  {error}
                </div>
              )}

              <div className="grid grid-cols-3 gap-4">
                <div>
                  <label className="text-[11px] font-medium block mb-1" style={{ color: 'var(--text-dimmed)' }}>Name</label>
                  <input className="input" value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. theorem-schema" />
                </div>
                <div>
                  <label className="text-[11px] font-medium block mb-1" style={{ color: 'var(--text-dimmed)' }}>Node Type</label>
                  <input className="input" value={nodeType} onChange={(e) => setNodeType(e.target.value)} placeholder="e.g. TheoremNode" />
                </div>
                <div>
                  <label className="text-[11px] font-medium block mb-1" style={{ color: 'var(--text-dimmed)' }}>Entity Type</label>
                  <select
                    className="input"
                    value={entityType}
                    onChange={(e) => setEntityType(e.target.value as 'node' | 'edge')}
                  >
                    <option value="node">Node</option>
                    <option value="edge">Edge</option>
                  </select>
                </div>
              </div>

              <div>
                <label className="text-[11px] font-medium block mb-1" style={{ color: 'var(--text-dimmed)' }}>Description</label>
                <input className="input" value={description} onChange={(e) => setDescription(e.target.value)} placeholder="Schema description" />
              </div>

              <div>
                <label className="text-[11px] font-medium block mb-1" style={{ color: 'var(--text-dimmed)' }}>Applicable Types (comma-separated)</label>
                <input className="input" value={applicableTypes} onChange={(e) => setApplicableTypes(e.target.value)} placeholder="theorem, lemma, definition" />
              </div>

              <div>
                <div className="flex items-center justify-between mb-1">
                  <label className="text-[11px] font-medium" style={{ color: 'var(--text-dimmed)' }}>JSON Schema</label>
                  {jsonError && (
                    <span className="text-[10px]" style={{ color: 'var(--accent-red)' }}>{jsonError}</span>
                  )}
                </div>
                <div className="rounded overflow-hidden" style={{ border: '1px solid var(--border-default)' }}>
                  <CodeMirror
                    value={schemaJson}
                    onChange={handleSchemaChange}
                    extensions={[json()]}
                    theme="dark"
                    height="300px"
                    basicSetup={{
                      lineNumbers: true,
                      foldGutter: true,
                      bracketMatching: true,
                      closeBrackets: true,
                    }}
                  />
                </div>
              </div>
            </div>

            {/* Footer */}
            <div className="flex items-center justify-end gap-2 px-5 py-3 shrink-0" style={{ borderTop: '1px solid var(--border-default)' }}>
              <button onClick={onClose} className="btn-secondary text-xs py-1.5 px-3">Cancel</button>
              <button
                onClick={handleSave}
                disabled={saving || !!jsonError}
                className="btn-neon-green text-xs gap-1.5 py-1.5 px-3 disabled:opacity-50"
              >
                {saving ? <Loader2 size={14} className="animate-spin" /> : <Save size={14} />}
                {schemaId ? 'Update' : 'Create'}
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  )
}
