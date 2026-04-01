import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useNyxID } from '@nyxids/oauth-react'

export default function AuthCallback() {
  const { handleRedirectCallback } = useNyxID()
  const navigate = useNavigate()
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    handleRedirectCallback()
      .then(() => {
        navigate('/graph', { replace: true })
      })
      .catch((err) => {
        console.error('Auth callback failed:', err)
        setError(err instanceof Error ? err.message : 'Authentication failed')
      })
  }, [handleRedirectCallback, navigate])

  if (error) {
    return (
      <div
        className="h-screen w-screen flex items-center justify-center"
        style={{ background: 'var(--bg-base)' }}
      >
        <div className="cyber-popup">
          <div className="cyber-popup-border" />
          <div className="flex flex-col items-center gap-3 py-2">
            <span className="text-sm" style={{ color: 'var(--neon-red)', textShadow: '0 0 8px rgba(255,68,68,0.3)' }}>
              {error}
            </span>
            <button
              onClick={() => navigate('/', { replace: true })}
              className="btn-neon-danger text-xs py-1 px-3"
            >
              Back to app
            </button>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div
      className="h-screen w-screen flex items-center justify-center"
      style={{ background: 'var(--bg-base)' }}
    >
      <div className="thinking-indicator">
        <div className="thinking-dots" style={{ '--dot-color': 'var(--neon-cyan)' } as React.CSSProperties}>
          <span />
          <span />
          <span />
        </div>
        <span className="text-xs" style={{ color: 'var(--text-dimmed)' }}>
          Completing sign in...
        </span>
      </div>
    </div>
  )
}
