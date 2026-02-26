import { useState } from 'react'
import { Copy, Check } from 'lucide-react'

function highlightYaml(yaml: string): string {
  return yaml
    .split('\n')
    .map((line) => {
      // Comments
      if (line.trimStart().startsWith('#')) {
        return `<span style="color:var(--text-dimmed);font-style:italic">${escapeHtml(line)}</span>`
      }
      // Key: value lines
      const match = line.match(/^(\s*)([\w.-]+)(:)(.*)$/)
      if (match) {
        const [, indent, key, colon, rest] = match
        let valueHtml = escapeHtml(rest)
        // Highlight string values
        if (rest.trim().startsWith('"') || rest.trim().startsWith("'")) {
          valueHtml = ` <span style="color:var(--accent-gold)">${escapeHtml(rest.trim())}</span>`
        } else if (/^\s*(true|false)$/i.test(rest)) {
          valueHtml = ` <span style="color:var(--accent-purple)">${escapeHtml(rest.trim())}</span>`
        } else if (/^\s*\d+(\.\d+)?$/.test(rest)) {
          valueHtml = ` <span style="color:var(--accent-orange)">${escapeHtml(rest.trim())}</span>`
        }
        return `${escapeHtml(indent)}<span style="color:var(--text-primary)">${escapeHtml(key)}</span><span style="color:var(--text-dimmed)">${escapeHtml(colon)}</span>${valueHtml}`
      }
      // List items
      const listMatch = line.match(/^(\s*)(- )(.*)$/)
      if (listMatch) {
        const [, indent, dash, rest] = listMatch
        return `${escapeHtml(indent)}<span style="color:var(--text-muted)">${escapeHtml(dash)}</span>${escapeHtml(rest)}`
      }
      return escapeHtml(line)
    })
    .join('\n')
}

function escapeHtml(str: string): string {
  return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}

interface YamlViewerProps {
  yaml: string | null
  loading?: boolean
}

export default function YamlViewer({ yaml, loading }: YamlViewerProps) {
  const [copied, setCopied] = useState(false)

  const handleCopy = async () => {
    if (!yaml) return
    await navigator.clipboard.writeText(yaml)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  if (loading) {
    return (
      <div className="h-full flex flex-col">
        <div className="flex items-center justify-between px-4 py-3">
          <h2 className="text-[13px] font-semibold" style={{ color: 'var(--text-muted)' }}>
            Workflow Definition
          </h2>
        </div>
        <div className="divider-h" />
        <div className="flex-1 p-4 space-y-2">
          {Array.from({ length: 12 }).map((_, i) => (
            <div key={i} className="skeleton h-4" style={{ width: `${60 + Math.random() * 30}%` }} />
          ))}
        </div>
      </div>
    )
  }

  return (
    <div className="h-full flex flex-col">
      <div className="flex items-center justify-between px-4 py-3">
        <h2 className="text-[13px] font-semibold" style={{ color: 'var(--text-muted)' }}>
          Workflow Definition
        </h2>
        <button
          onClick={handleCopy}
          className="icon-btn"
          title="Copy YAML"
        >
          {copied ? <Check size={14} style={{ color: 'var(--accent-green)' }} /> : <Copy size={14} />}
        </button>
      </div>
      <div className="divider-h" />
      <div className="flex-1 overflow-auto p-4">
        <pre
          className="font-mono text-xs leading-relaxed whitespace-pre-wrap"
          style={{ color: 'var(--text-secondary)' }}
          dangerouslySetInnerHTML={{ __html: yaml ? highlightYaml(yaml) : '' }}
        />
      </div>
    </div>
  )
}
