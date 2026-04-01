import { useCallback, useMemo } from 'react'
import CodeMirror from '@uiw/react-codemirror'
import { yaml } from '@codemirror/lang-yaml'
import {
  EditorView,
  Decoration,
  type DecorationSet,
  ViewPlugin,
  type ViewUpdate,
  hoverTooltip,
  type Tooltip,
  keymap,
} from '@codemirror/view'
import { RangeSetBuilder } from '@codemirror/state'
import {
  autocompletion,
  startCompletion,
  type CompletionContext,
  type CompletionResult,
} from '@codemirror/autocomplete'

interface SchemaRecord {
  name: string
  jsonSchema?: Record<string, unknown>
}

interface YamlEditorProps {
  value: string
  onChange: (value: string) => void
  readOnly?: boolean
  schemas?: SchemaRecord[]
}

// ── Schema reference highlighting ──

const schemaRefMark = Decoration.mark({ class: 'cm-schema-ref' })

function buildSchemaDecos(view: EditorView, names: Set<string>): DecorationSet {
  if (names.size === 0) return Decoration.none
  const builder = new RangeSetBuilder<Decoration>()
  for (let i = 0; i < view.state.doc.lines; i++) {
    const line = view.state.doc.line(i + 1)
    const regex = /\/([a-zA-Z0-9_-]+)/g
    let m
    while ((m = regex.exec(line.text)) !== null) {
      if (names.has(m[1])) {
        builder.add(
          line.from + m.index,
          line.from + m.index + m[0].length,
          schemaRefMark,
        )
      }
    }
  }
  return builder.finish()
}

function schemaHighlightPlugin(names: Set<string>) {
  return ViewPlugin.fromClass(
    class {
      decorations: DecorationSet
      constructor(view: EditorView) {
        this.decorations = buildSchemaDecos(view, names)
      }
      update(update: ViewUpdate) {
        if (update.docChanged || update.viewportChanged) {
          this.decorations = buildSchemaDecos(update.view, names)
        }
      }
    },
    { decorations: (v) => v.decorations },
  )
}

// ── Hover tooltip ──

function schemaHoverTooltip(schemasMap: Map<string, SchemaRecord>) {
  return hoverTooltip((view, pos) => {
    const line = view.state.doc.lineAt(pos)
    const offset = pos - line.from
    const regex = /\/([a-zA-Z0-9_-]+)/g
    let m
    while ((m = regex.exec(line.text)) !== null) {
      const start = m.index
      const end = start + m[0].length
      if (offset >= start && offset <= end) {
        const schema = schemasMap.get(m[1])
        if (!schema) return null
        return {
          pos: line.from + start,
          end: line.from + end,
          above: true,
          create() {
            const dom = document.createElement('div')
            dom.style.cssText = 'max-width:450px;max-height:320px;overflow:auto;padding:8px 10px;font-size:11px;font-family:monospace;white-space:pre;background:#1a1a1e;color:#ccc;border:1px solid #333;border-radius:6px;box-shadow:0 4px 20px rgba(0,0,0,0.5);'

            const header = document.createElement('div')
            header.style.cssText = 'font-weight:600;margin-bottom:6px;color:var(--neon-cyan);font-size:12px;'
            header.textContent = schema.name
            dom.appendChild(header)

            const content = document.createElement('pre')
            content.style.cssText = 'margin:0;white-space:pre-wrap;word-break:break-word;font-size:10px;'
            content.textContent = schema.jsonSchema
              ? JSON.stringify(schema.jsonSchema, null, 2)
              : '(no schema definition)'
            dom.appendChild(content)

            return { dom }
          },
        } satisfies Tooltip
      }
    }
    return null
  })
}

// ── Slash autocomplete ──

function schemaCompletion(schemas: SchemaRecord[]) {
  return (context: CompletionContext): CompletionResult | null => {
    // Match "/" followed by optional partial name
    const word = context.matchBefore(/\/[a-zA-Z0-9_-]*/)
    if (!word) return null

    const typed = word.text.slice(1).toLowerCase() // remove leading /

    return {
      from: word.from,
      options: schemas
        .filter((s) => s.name.toLowerCase().includes(typed))
        .map((s) => ({
          label: `/${s.name}`,
          type: 'keyword',
          detail: s.jsonSchema ? 'schema' : '',
          info: s.jsonSchema ? JSON.stringify(s.jsonSchema, null, 2).slice(0, 200) + '...' : undefined,
          boost: s.name.toLowerCase().startsWith(typed) ? 1 : 0,
        })),
      filter: false,
    }
  }
}

// Trigger autocomplete on "/" keystroke
function slashTriggerKeymap() {
  return keymap.of([
    {
      key: '/',
      run: (view) => {
        // Insert the "/" character first
        view.dispatch(view.state.replaceSelection('/'))
        // Then trigger autocomplete
        startCompletion(view)
        return true
      },
    },
  ])
}

// ── Theme ──

const schemaRefTheme = EditorView.baseTheme({
  '.cm-schema-ref': {
    background: 'rgba(125,211,252,0.12)',
    color: 'var(--neon-cyan) !important',
    borderRadius: '3px',
    padding: '0 2px',
    cursor: 'help',
  },
  '.cm-tooltip-autocomplete': {
    background: '#1a1a1e !important',
    border: '1px solid #333 !important',
    borderRadius: '6px !important',
    boxShadow: '0 4px 20px rgba(0,0,0,0.5) !important',
  },
  '.cm-tooltip-autocomplete ul li': {
    color: '#ccc !important',
    padding: '4px 8px !important',
  },
  '.cm-tooltip-autocomplete ul li[aria-selected]': {
    background: 'rgba(125,211,252,0.15) !important',
    color: 'var(--neon-cyan) !important',
  },
  '.cm-tooltip-autocomplete .cm-completionLabel': {
    color: 'var(--neon-cyan) !important',
  },
  '.cm-tooltip-autocomplete .cm-completionDetail': {
    color: '#888 !important',
    fontStyle: 'normal !important',
    marginLeft: '8px',
  },
  '.cm-completionInfo': {
    background: '#1a1a1e !important',
    border: '1px solid #333 !important',
    borderRadius: '6px !important',
    color: '#aaa !important',
    padding: '6px 8px !important',
    fontSize: '10px !important',
    fontFamily: 'monospace !important',
    maxHeight: '200px !important',
    whiteSpace: 'pre-wrap !important',
  },
})

export default function YamlEditor({ value, onChange, readOnly, schemas }: YamlEditorProps) {
  const handleChange = useCallback(
    (val: string) => {
      onChange(val)
    },
    [onChange],
  )

  const extensions = useMemo(() => {
    const exts = [yaml(), EditorView.lineWrapping, schemaRefTheme]
    if (schemas && schemas.length > 0) {
      const nameSet = new Set(schemas.map((s) => s.name))
      const nameMap = new Map(schemas.map((s) => [s.name, s]))
      exts.push(schemaHighlightPlugin(nameSet))
      exts.push(schemaHoverTooltip(nameMap))
      exts.push(autocompletion({ override: [schemaCompletion(schemas)] }))
      exts.push(slashTriggerKeymap())
    }
    return exts
  }, [schemas])

  return (
    <div className="h-full w-full overflow-auto" style={{ background: 'var(--bg-base)' }}>
      <CodeMirror
        value={value}
        onChange={handleChange}
        extensions={extensions}
        theme="dark"
        height="100%"
        readOnly={readOnly}
        basicSetup={{
          lineNumbers: true,
          foldGutter: true,
          highlightActiveLine: true,
          bracketMatching: true,
          indentOnInput: true,
          completionKeymap: true,
        }}
      />
    </div>
  )
}
