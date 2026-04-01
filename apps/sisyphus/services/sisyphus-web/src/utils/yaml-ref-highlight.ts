import { Decoration, DecorationSet, EditorView, ViewPlugin, ViewUpdate } from '@codemirror/view'
import { RangeSetBuilder } from '@codemirror/state'

const skillDeco = Decoration.mark({ class: 'cm-skill-ref' })
const schemaDeco = Decoration.mark({ class: 'cm-schema-ref' })
const REF_RE = /\/(?:skill|schema)-[a-zA-Z0-9_-]+/g

function buildDecorations(view: EditorView): DecorationSet {
  const builder = new RangeSetBuilder<Decoration>()
  for (const { from, to } of view.visibleRanges) {
    const text = view.state.doc.sliceString(from, to)
    for (const match of text.matchAll(REF_RE)) {
      const start = from + match.index!
      const end = start + match[0].length
      const deco = match[0].startsWith('/skill-') ? skillDeco : schemaDeco
      builder.add(start, end, deco)
    }
  }
  return builder.finish()
}

export const refHighlightPlugin = ViewPlugin.fromClass(
  class {
    decorations: DecorationSet
    constructor(view: EditorView) { this.decorations = buildDecorations(view) }
    update(update: ViewUpdate) {
      if (update.docChanged || update.viewportChanged) this.decorations = buildDecorations(update.view)
    }
  },
  { decorations: (v) => v.decorations },
)

export const refHighlightTheme = EditorView.baseTheme({
  '.cm-skill-ref': {
    color: '#e9d5ff',
    background: 'rgba(196,181,253,0.3)',
    borderRadius: '3px',
    padding: '1px 3px',
    fontWeight: '600',
  },
  '.cm-schema-ref': {
    color: '#bbf7d0',
    background: 'rgba(134,239,172,0.3)',
    borderRadius: '3px',
    padding: '1px 3px',
    fontWeight: '600',
  },
})
