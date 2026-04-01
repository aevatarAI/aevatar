import { type ReactNode } from 'react'
import { useAuth } from './useAuth'

interface ProtectedRouteProps {
  readonly children: ReactNode
}

export default function ProtectedRoute({ children }: ProtectedRouteProps) {
  const { isAuthenticated, login } = useAuth()

  if (!isAuthenticated) {
    return (
      <div
        className="h-screen w-screen flex items-center justify-center"
        style={{ background: 'var(--bg-base)' }}
      >
        <div className="cyber-popup">
          <div className="cyber-popup-border" />
          <div className="flex flex-col items-center gap-5 py-4">
            <span
              className="text-sm font-semibold"
              style={{ color: 'var(--text-muted)' }}
            >
              Sign in to access Sisyphus
            </span>
            <button onClick={login} className="btn-neon text-sm py-2 px-6">
              Sign in with NyxID
            </button>
          </div>
        </div>
      </div>
    )
  }

  return <>{children}</>
}
