import { useState, useEffect, useRef, useCallback } from 'react'
import { Outlet, useLocation, useNavigate } from 'react-router-dom'
import TopNav from './TopNav'
import GraphPage from '../GraphPage'

export default function AppLayout() {
  const location = useLocation()
  const navigate = useNavigate()
  const isGraphPage = location.pathname === '/graph' || location.pathname === '/'

  // Redirect root to /graph
  useEffect(() => {
    if (location.pathname === '/') navigate('/graph', { replace: true })
  }, [location.pathname, navigate])

  const [mounted, setMounted] = useState(!isGraphPage)
  const [visible, setVisible] = useState(!isGraphPage)
  const [settled, setSettled] = useState(!isGraphPage) // true after enter animation done — enables blur
  const wasGraphRef = useRef(isGraphPage)

  useEffect(() => {
    if (!isGraphPage && wasGraphRef.current) {
      setMounted(true)
      setSettled(false)
      requestAnimationFrame(() => requestAnimationFrame(() => setVisible(true)))
    } else if (isGraphPage && !wasGraphRef.current) {
      setSettled(false)
      setVisible(false)
    } else if (!isGraphPage) {
      setMounted(true)
      setVisible(true)
      setSettled(true)
    }
    wasGraphRef.current = isGraphPage
  }, [isGraphPage, location.pathname])

  const handleTransitionEnd = useCallback(() => {
    if (!visible && isGraphPage) setMounted(false)
    if (visible && !isGraphPage) setSettled(true)
  }, [visible, isGraphPage])

  return (
    <div className="h-screen w-screen flex flex-col overflow-hidden" style={{ background: 'var(--bg-base)' }}>
      <TopNav />
      <div className="flex-1 overflow-hidden relative">
        <div className="absolute inset-0">
          <GraphPage />
        </div>

        {mounted && (
          <div
            className="absolute inset-0 z-10 flex justify-center"
            onTransitionEnd={handleTransitionEnd}
            style={{
              // Only apply blur after animation settles — blur during animation kills FPS
              background: settled ? 'rgba(12, 15, 20, 0.55)' : 'rgba(12, 15, 20, 0.7)',
              backdropFilter: settled ? 'blur(20px) saturate(1.1)' : 'none',
              WebkitBackdropFilter: settled ? 'blur(20px) saturate(1.1)' : 'none',
              transform: visible ? 'translateY(0)' : 'translateY(-100%)',
              opacity: visible ? 1 : 0,
              transition: 'transform 0.4s cubic-bezier(0.32, 0.72, 0, 1), opacity 0.3s ease',
              willChange: 'transform, opacity',
            }}
          >
            <div className="w-[85%] h-full flex flex-col overflow-hidden"
              style={{ borderLeft: '1px solid rgba(148,163,184,0.06)', borderRight: '1px solid rgba(148,163,184,0.06)' }}>
              <Outlet />
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
