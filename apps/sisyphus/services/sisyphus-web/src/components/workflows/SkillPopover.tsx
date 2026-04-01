import { useState, useEffect, type CSSProperties } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { Loader2 } from 'lucide-react'
import { useAuth } from '../../auth/useAuth'
import { fetchSkillContent } from '../../api/ornn-api'

interface SkillPopoverProps {
  skillId: string
  style?: CSSProperties
}

export default function SkillPopover({ skillId, style }: SkillPopoverProps) {
  const { getAccessToken } = useAuth()
  const [content, setContent] = useState<string | null>(null)
  const [skillName, setSkillName] = useState<string>('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const token = getAccessToken()
    if (!token) {
      setError('Not authenticated')
      setLoading(false)
      return
    }

    setLoading(true)
    setError(null)
    fetchSkillContent(skillId, token)
      .then((skill) => {
        setContent(skill.content ?? 'No content available')
        setSkillName(skill.name)
      })
      .catch((err) => {
        setError(err instanceof Error ? err.message : 'Failed to load skill')
      })
      .finally(() => setLoading(false))
  }, [skillId, getAccessToken])

  return (
    <div
      className="z-50 w-80 max-h-96 overflow-auto rounded-lg shadow-lg animate-scale-in"
      style={{
        background: 'rgba(14, 14, 16, 0.98)',
        border: '1px solid rgba(191, 127, 255, 0.3)',
        boxShadow: '0 0 20px rgba(191, 127, 255, 0.1)',
        backdropFilter: 'blur(12px)',
        ...style,
      }}
    >
      {/* Header */}
      <div className="px-3 py-2 shrink-0" style={{ borderBottom: '1px solid var(--border-default)' }}>
        <div className="text-[11px] font-semibold" style={{ color: '#bf7fff' }}>
          {skillName || skillId}
        </div>
      </div>

      {/* Content */}
      <div className="px-3 py-2">
        {loading && (
          <div className="flex items-center gap-2 py-4 justify-center">
            <Loader2 size={14} className="animate-spin" style={{ color: 'var(--text-dimmed)' }} />
          </div>
        )}

        {error && (
          <div className="text-[11px]" style={{ color: 'var(--accent-red)' }}>{error}</div>
        )}

        {content && !loading && (
          <div className="text-[11px] leading-relaxed prose-invert max-w-none" style={{ color: 'var(--text-secondary)' }}>
            <ReactMarkdown
              remarkPlugins={[remarkGfm]}
              components={{
                h1: ({ children }) => <h1 className="text-sm font-bold mt-2 mb-1" style={{ color: 'var(--text-primary)' }}>{children}</h1>,
                h2: ({ children }) => <h2 className="text-xs font-semibold mt-2 mb-1" style={{ color: 'var(--text-primary)' }}>{children}</h2>,
                h3: ({ children }) => <h3 className="text-[11px] font-semibold mt-1 mb-0.5" style={{ color: 'var(--text-primary)' }}>{children}</h3>,
                p: ({ children }) => <p className="mb-1.5">{children}</p>,
                code: ({ children }) => (
                  <code className="text-[10px] font-mono px-1 py-0.5 rounded" style={{ background: 'var(--bg-accent)', color: 'var(--neon-cyan)' }}>
                    {children}
                  </code>
                ),
                pre: ({ children }) => (
                  <pre className="text-[10px] font-mono p-2 rounded my-1 overflow-auto" style={{ background: 'var(--bg-base)', border: '1px solid var(--border-subtle)' }}>
                    {children}
                  </pre>
                ),
              }}
            >
              {content}
            </ReactMarkdown>
          </div>
        )}
      </div>
    </div>
  )
}
