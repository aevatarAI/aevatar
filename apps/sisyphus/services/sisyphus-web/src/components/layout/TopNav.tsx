import { NavLink } from 'react-router-dom'
import { LogOut } from 'lucide-react'
import { useAuth } from '../../auth/useAuth'
import sisyphusLogo from '../../assets/sisyphus_logo.svg'

const NAV_ITEMS = [
  { to: '/graph', label: 'Graph' },
  { to: '/workflows', label: 'Workflows' },
  { to: '/schemas', label: 'Schemas' },
  { to: '/upload', label: 'Upload' },
  { to: '/settings', label: 'Settings' },
]

export default function TopNav() {
  const { user, logout } = useAuth()

  return (
    <header className="z-30 shrink-0 relative" style={{ background: 'rgba(12, 15, 20, 0.6)', backdropFilter: 'blur(20px)', borderBottom: '1px solid rgba(148,163,184,0.1)' }}>
      <div className="flex items-center justify-between px-2 py-0">
        <div className="flex items-center gap-1">
          <NavLink to="/graph" className="flex items-center py-3">
            <img src={sisyphusLogo} alt="Sisyphus" className="h-8" />
          </NavLink>

          <nav className="flex items-center gap-1">
            {NAV_ITEMS.map(({ to, label }) => (
              <NavLink key={to} to={to} className="relative px-4 py-4 text-sm font-medium transition-colors"
                style={({ isActive }) => ({ color: isActive ? 'var(--neon-cyan)' : 'var(--text-dimmed)' })}>
                {({ isActive }) => (
                  <>
                    {label}
                    {isActive && (
                      <div className="absolute bottom-0 left-4 right-4 h-[2px] rounded-full"
                        style={{ background: 'var(--neon-cyan)', boxShadow: '0 0 8px rgba(125,211,252,0.5)' }} />
                    )}
                  </>
                )}
              </NavLink>
            ))}
          </nav>
        </div>

        <div className="flex items-center gap-3">
          {user?.picture ? (
            <img src={user.picture} alt={user.name ?? 'User'} className="w-8 h-8 rounded-full" style={{ border: '1px solid rgba(148,163,184,0.2)' }} />
          ) : (
            <div className="w-8 h-8 rounded-full flex items-center justify-center text-xs font-semibold"
              style={{ background: 'rgba(125,211,252,0.1)', color: 'var(--neon-cyan)', border: '1px solid rgba(125,211,252,0.2)' }}>
              {(user?.name ?? user?.email ?? '?')[0].toUpperCase()}
            </div>
          )}
          <button onClick={logout} className="icon-btn p-2" title="Sign out" style={{ color: 'var(--text-dimmed)' }}>
            <LogOut size={16} />
          </button>
        </div>
      </div>
    </header>
  )
}
