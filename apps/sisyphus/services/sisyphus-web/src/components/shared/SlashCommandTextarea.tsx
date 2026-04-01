import { useState, useRef, useEffect, useCallback, useMemo } from 'react'
import { createPortal } from 'react-dom'
import { Zap, FileJson, Lock, Globe } from 'lucide-react'
import CodeMirror, { type ReactCodeMirrorRef } from '@uiw/react-codemirror'
import { EditorView, type ViewUpdate } from '@codemirror/view'
import { useAuth } from '../../auth/useAuth'
import { searchSkills, type OrnnSkill } from '../../api/ornn-api'
import { fetchSchemas } from '../../api/schemas-api'
import type { SchemaListItem } from '../../types/schema'
import { refHighlightPlugin, refHighlightTheme } from '../../utils/yaml-ref-highlight'

interface SlashCommandTextareaProps {
  value: string
  onChange: (value: string) => void
  placeholder?: string
  rows?: number
  className?: string
}

function Section({ icon: Icon, label, color, count, children }: {
  icon: typeof Zap; label: string; color: string; count: number; children: React.ReactNode
}) {
  return (
    <div>
      <div className="sticky top-0 flex items-center gap-1.5 px-3 py-1.5" style={{ background: 'var(--bg-accent)', borderBottom: '1px solid var(--border-subtle)' }}>
        <Icon size={10} style={{ color }} />
        <span className="text-[9px] font-semibold uppercase tracking-wider" style={{ color }}>{label} ({count})</span>
      </div>
      {children}
    </div>
  )
}

function SuggestionItem({ name, description, onClick }: { name: string; description?: string; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick}
      className="w-full flex flex-col gap-0.5 px-3 py-1.5 text-left transition-colors hover:bg-[var(--bg-elevated)]">
      <span className="text-[11px] font-mono truncate" style={{ color: 'var(--text-primary)' }}>{name}</span>
      {description && <span className="text-[9px] truncate" style={{ color: 'var(--text-dimmed)' }}>{description}</span>}
    </button>
  )
}

// CodeMirror theme for the prompt editor
const promptTheme = EditorView.theme({
  '&': { fontSize: '12px', background: 'var(--bg-base)' },
  '.cm-content': { fontFamily: '"JetBrains Mono", "Space Grotesk", monospace', padding: '12px 16px' },
  '.cm-gutters': { display: 'none' },
  '.cm-activeLine': { backgroundColor: 'transparent' },
  '.cm-selectionBackground': { backgroundColor: 'rgba(125,211,252,0.2) !important' },
  '&.cm-focused': { outline: 'none' },
  // Make skill/schema refs clickable-looking on hover
  '.cm-skill-ref:hover': { textDecoration: 'underline', cursor: 'pointer' },
  '.cm-schema-ref:hover': { textDecoration: 'underline', cursor: 'pointer' },
})

export default function SlashCommandTextarea({ value, onChange, placeholder, rows = 4, className = '' }: SlashCommandTextareaProps) {
  const { getAccessToken } = useAuth()
  const editorRef = useRef<HTMLDivElement>(null)
  const [showPopup, setShowPopup] = useState(false)
  const [slashStart, setSlashStart] = useState(-1)
  const [query, setQuery] = useState('')
  const [pos, setPos] = useState({ top: 0, left: 0 })

  // Hover popover
  const [hoverRef, setHoverRef] = useState<{ ref: string; x: number; y: number } | null>(null)
  const hoverTimeout = useRef<number | null>(null)

  const [skills, setSkills] = useState<OrnnSkill[]>([])
  const [schemas, setSchemas] = useState<SchemaListItem[]>([])
  const loadedRef2 = useRef(false)
  const token = getAccessToken()

  useEffect(() => {
    if (!token || loadedRef2.current) return
    loadedRef2.current = true
    searchSkills('', token).then(setSkills).catch(() => {})
    fetchSchemas(token).then(setSchemas).catch(() => {})
  }, [token])

  const q = query.toLowerCase()
  const privateSkills = useMemo(() => skills.filter((s) => s.isPrivate && (!q || s.name.toLowerCase().includes(q))), [skills, q])
  const publicSkills = useMemo(() => skills.filter((s) => !s.isPrivate && (!q || s.name.toLowerCase().includes(q))), [skills, q])
  const nodeSchemas = useMemo(() => schemas.filter((s) => s.entityType === 'node' && (!q || s.name.toLowerCase().includes(q))), [schemas, q])
  const edgeSchemas = useMemo(() => schemas.filter((s) => s.entityType === 'edge' && (!q || s.name.toLowerCase().includes(q))), [schemas, q])
  const hasResults = privateSkills.length + publicSkills.length + nodeSchemas.length + edgeSchemas.length > 0

  const cmRef = useRef<ReactCodeMirrorRef>(null)

  const handleChange = useCallback((val: string) => { onChange(val) }, [onChange])

  // Detect slash commands from cursor position via onUpdate
  const handleUpdate = useCallback((vu: ViewUpdate) => {
    if (!vu.docChanged && !vu.selectionSet) return
    const cursorPos = vu.state.selection.main.head
    const text = vu.state.doc.toString()
    const textBefore = text.slice(0, cursorPos)
    const lastSlash = textBefore.lastIndexOf('/')

    if (lastSlash >= 0 && (lastSlash === 0 || /\s/.test(textBefore[lastSlash - 1]))) {
      const typed = textBefore.slice(lastSlash + 1)
      if (typed.length <= 40 && !/\s/.test(typed)) {
        setSlashStart(lastSlash)
        setQuery(typed)
        setShowPopup(true)
        if (editorRef.current) {
          const rect = editorRef.current.getBoundingClientRect()
          setPos({ top: rect.bottom + 4, left: rect.left })
        }
        return
      }
    }
    setShowPopup(false)
  }, [])

  const applySuggestion = useCallback((type: 'skill' | 'schema', name: string) => {
    const prefix = type === 'skill' ? '/skill-' : '/schema-'
    const replacement = `${prefix}${name} `
    const view = cmRef.current?.view
    if (view) {
      const cursorPos = view.state.selection.main.head
      view.dispatch({
        changes: { from: slashStart, to: cursorPos, insert: replacement },
        selection: { anchor: slashStart + replacement.length },
      })
    } else {
      // Fallback
      const before = value.slice(0, slashStart)
      onChange(before + replacement + value.slice(value.length))
    }
    setShowPopup(false)
  }, [value, slashStart, onChange])

  // Hover on refs in the editor — fix position on first enter, don't follow mouse
  const currentHoverRef = useRef<string | null>(null)
  const handleEditorMouseMove = useCallback((e: React.MouseEvent) => {
    const target = e.target as HTMLElement
    if (target.classList.contains('cm-skill-ref') || target.classList.contains('cm-schema-ref')) {
      const text = target.textContent ?? ''
      if (hoverTimeout.current) clearTimeout(hoverTimeout.current)
      // Only update position when hovering a NEW ref
      if (currentHoverRef.current !== text) {
        currentHoverRef.current = text
        const rect = target.getBoundingClientRect()
        setHoverRef({ ref: text, x: rect.left, y: rect.bottom + 4 })
      }
    } else {
      currentHoverRef.current = null
      if (hoverTimeout.current) clearTimeout(hoverTimeout.current)
      hoverTimeout.current = window.setTimeout(() => setHoverRef(null), 200)
    }
  }, [])

  const handleEditorMouseLeave = useCallback(() => {
    if (hoverTimeout.current) clearTimeout(hoverTimeout.current)
    hoverTimeout.current = window.setTimeout(() => setHoverRef(null), 300)
  }, [])

  // Preview content for hovered skill
  const [previewContent, setPreviewContent] = useState<string | null>(null)
  const previewFetchedRef = useRef<string | null>(null)

  useEffect(() => {
    if (!hoverRef || !token) { setPreviewContent(null); previewFetchedRef.current = null; return }
    const isSkill = hoverRef.ref.startsWith('/skill-')
    if (!isSkill) {
      // For schemas, show the schema's JSON preview
      const name = hoverRef.ref.slice(8)
      const schema = schemas.find((s) => s.name === name)
      if (schema?.jsonSchema) {
        const jsonStr = JSON.stringify(schema.jsonSchema, null, 2)
        const lines = jsonStr.split('\n').slice(0, 3)
        setPreviewContent(lines.join('\n') + (jsonStr.split('\n').length > 3 ? '\n...' : ''))
      } else { setPreviewContent(null) }
      return
    }
    const name = hoverRef.ref.slice(7)
    if (previewFetchedRef.current === name) return // already fetched
    previewFetchedRef.current = name
    setPreviewContent(null)
    // Use /api/web/skills/:name (optional auth, no permission required) for preview
    fetch(`https://ornn-api.chrono-ai.fun/api/web/skills/${encodeURIComponent(name)}`, {
      headers: token ? { Authorization: `Bearer ${token}` } : {},
    })
      .then((r) => r.ok ? r.json() : Promise.reject())
      .then((body: { data?: { description?: string; readme?: string } }) => {
        const desc = body.data?.description ?? body.data?.readme ?? ''
        if (desc) {
          const lines = desc.split('\n').filter((l: string) => l.trim()).slice(0, 3)
          setPreviewContent(lines.join('\n') + (desc.split('\n').filter((l: string) => l.trim()).length > 3 ? '\n...' : ''))
        } else { setPreviewContent(null) }
      })
      .catch(() => setPreviewContent(null))
  }, [hoverRef, token, schemas])

  const getRefLink = useCallback((ref: string) => {
    const isSkill = ref.startsWith('/skill-')
    const name = ref.slice(isSkill ? 7 : 8)
    if (isSkill) return { label: 'Open in Ornn', url: `https://ornn.chrono-ai.fun/skills/${encodeURIComponent(name)}`, external: true }
    const schema = schemas.find((s) => s.name === name)
    return { label: 'Edit schema', url: `/schemas/${schema?.id ?? name}/edit`, external: false }
  }, [schemas])

  const height = `${Math.max(rows * 22 + 24, 100)}px`

  return (
    <>
      <div ref={editorRef} className={`rounded-lg overflow-hidden ${className}`}
        style={{ border: '1px solid var(--border-default)' }}
        onMouseMove={handleEditorMouseMove} onMouseLeave={handleEditorMouseLeave}>
        <CodeMirror ref={cmRef}
          value={value} onChange={handleChange} onUpdate={handleUpdate}
          extensions={[refHighlightPlugin, refHighlightTheme, promptTheme, EditorView.lineWrapping]}
          theme="dark" height={height} placeholder={placeholder ?? 'Type / for skills & schemas'}
          basicSetup={{ lineNumbers: false, foldGutter: false, highlightActiveLine: false, bracketMatching: false }}
        />
      </div>

      {/* Hover popover */}
      {hoverRef && createPortal(
        <div className="fixed z-[9999] animate-scale-in" style={{ top: hoverRef.y, left: hoverRef.x }}
          onMouseEnter={() => { if (hoverTimeout.current) clearTimeout(hoverTimeout.current) }}
          onMouseLeave={() => setHoverRef(null)}>
          <div className="rounded-lg p-3 space-y-2" style={{ background: 'var(--bg-surface)', border: '1px solid var(--border-default)', boxShadow: 'var(--shadow-lg)', minWidth: 220, maxWidth: 360 }}>
            <div className="text-xs font-mono font-semibold" style={{ color: 'var(--text-primary)' }}>{hoverRef.ref}</div>
            {previewContent && (
              <pre className="text-[10px] leading-relaxed whitespace-pre-wrap rounded px-2 py-1.5"
                style={{ background: 'var(--bg-base)', color: 'var(--text-dimmed)', maxHeight: 80, overflow: 'hidden', fontFamily: '"JetBrains Mono", monospace' }}>
                {previewContent}
              </pre>
            )}
            {(() => {
              const { label, url, external } = getRefLink(hoverRef.ref)
              const color = hoverRef.ref.startsWith('/skill-') ? 'var(--neon-purple)' : 'var(--neon-green)'
              return external
                ? <a href={url} target="_blank" rel="noopener noreferrer" className="text-[11px] font-medium hover:underline" style={{ color }}>{label} →</a>
                : <a href={url} className="text-[11px] font-medium hover:underline" style={{ color }}>{label} →</a>
            })()}
          </div>
        </div>,
        document.body,
      )}

      {/* Slash command popup */}
      {showPopup && hasResults && createPortal(
        <div className="fixed z-[9999] rounded-lg overflow-hidden animate-scale-in"
          style={{ top: pos.top, left: pos.left, width: Math.min(640, window.innerWidth - pos.left - 20),
            background: 'var(--bg-surface)', border: '1px solid var(--border-default)', boxShadow: 'var(--shadow-lg)', maxHeight: 360 }}>
          <div className="flex" style={{ maxHeight: 360 }}>
            <div className="flex-1 min-w-0 overflow-auto" style={{ borderRight: '1px solid var(--border-subtle)' }}>
              {privateSkills.length > 0 && <Section icon={Lock} label="My Skills" color="var(--neon-purple)" count={privateSkills.length}>
                {privateSkills.map((s) => <SuggestionItem key={s.guid} name={s.name} description={s.description} onClick={() => applySuggestion('skill', s.name)} />)}
              </Section>}
              {publicSkills.length > 0 && <Section icon={Globe} label="Public Skills" color="var(--neon-cyan)" count={publicSkills.length}>
                {publicSkills.map((s) => <SuggestionItem key={s.guid} name={s.name} description={s.description} onClick={() => applySuggestion('skill', s.name)} />)}
              </Section>}
              {!privateSkills.length && !publicSkills.length && <div className="px-3 py-4 text-center"><Zap size={14} style={{ color: 'var(--text-dimmed)' }} className="mx-auto mb-1" /><p className="text-[10px]" style={{ color: 'var(--text-dimmed)' }}>No skills found</p></div>}
            </div>
            <div className="flex-1 min-w-0 overflow-auto">
              {nodeSchemas.length > 0 && <Section icon={FileJson} label="Node Schemas" color="var(--neon-green)" count={nodeSchemas.length}>
                {nodeSchemas.map((s) => <SuggestionItem key={s.id} name={s.name} description={s.description} onClick={() => applySuggestion('schema', s.name)} />)}
              </Section>}
              {edgeSchemas.length > 0 && <Section icon={FileJson} label="Edge Schemas" color="var(--neon-gold)" count={edgeSchemas.length}>
                {edgeSchemas.map((s) => <SuggestionItem key={s.id} name={s.name} description={s.description} onClick={() => applySuggestion('schema', s.name)} />)}
              </Section>}
              {!nodeSchemas.length && !edgeSchemas.length && <div className="px-3 py-4 text-center"><FileJson size={14} style={{ color: 'var(--text-dimmed)' }} className="mx-auto mb-1" /><p className="text-[10px]" style={{ color: 'var(--text-dimmed)' }}>No schemas found</p></div>}
            </div>
          </div>
        </div>,
        document.body,
      )}
    </>
  )
}
