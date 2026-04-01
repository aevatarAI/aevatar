import { useState, useEffect, useCallback, useRef } from 'react'
import { X, Loader2, Play, Square, FileText, File } from 'lucide-react'
import { proxyUrl } from '../hooks/use-api'
import { useAuth } from '../auth/useAuth'
import type { GraphSnapshot } from '../types/graph'

interface CompileStep {
  step: number
  label: string
  status: 'pending' | 'running' | 'done' | 'error'
  detail?: string
}

interface HistoryItem {
  id: string
  filterName: string
  nodeCount: number
  edgeCount: number
  status: string
  latexFileName?: string
  pdfFileName?: string
  startedAt: string
  completedAt?: string
  error?: string
}

const STEPS: CompileStep[] = [
  { step: 1, label: 'Collecting nodes and edges', status: 'pending' },
  { step: 2, label: 'Sanitizing LaTeX content', status: 'pending' },
  { step: 3, label: 'Computing topological ordering', status: 'pending' },
  { step: 4, label: 'Generating LaTeX document', status: 'pending' },
  { step: 5, label: 'Compiling LaTeX to PDF (tectonic)', status: 'pending' },
  { step: 6, label: 'Uploading to storage', status: 'pending' },
]

interface CompilePopupProps {
  snapshot: GraphSnapshot | null
  filterName: string
  onClose: () => void
}

export default function CompilePopup({ snapshot, filterName, onClose }: CompilePopupProps) {
  const { getAccessToken } = useAuth()
  const [steps, setSteps] = useState<CompileStep[]>(STEPS.map((s) => ({ ...s })))
  const [compiling, setCompiling] = useState(false)
  const [compileId, setCompileId] = useState<string | null>(null)
  const [compileError, setCompileError] = useState<string | null>(null)
  const [compileResult, setCompileResult] = useState<Record<string, unknown> | null>(null)
  const [history, setHistory] = useState<HistoryItem[]>([])
  const [historyLoading, setHistoryLoading] = useState(false)
  const abortRef = useRef(false)
  const eventSourceRef = useRef<AbortController | null>(null)

  const SERVICE = 'sisyphus-paper-compiler'

  // Load history on mount
  const loadHistory = useCallback(async () => {
    const token = getAccessToken()
    if (!token) return
    setHistoryLoading(true)
    try {
      const res = await fetch(proxyUrl(SERVICE, '/compile-history'), {
        headers: { Authorization: `Bearer ${token}` },
      })
      if (res.ok) {
        const data = await res.json()
        setHistory(data.items ?? [])
      }
    } catch { /* ignore */ }
    finally { setHistoryLoading(false) }
  }, [getAccessToken])

  useEffect(() => { loadHistory() }, [loadHistory])

  // Start compile
  const handleStart = useCallback(async () => {
    if (!snapshot || snapshot.nodes.length === 0) return
    const token = getAccessToken()
    if (!token) return

    setCompiling(true)
    setCompileError(null)
    setCompileResult(null)
    setSteps(STEPS.map((s) => ({ ...s })))
    abortRef.current = false

    const controller = new AbortController()
    eventSourceRef.current = controller

    try {
      const res = await fetch(proxyUrl(SERVICE, '/compile-stream'), {
        method: 'POST',
        headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({
          nodeIds: snapshot.nodes.map((n) => n.id),
          edges: snapshot.edges.map((e) => ({ source: e.source, target: e.target, edge_type: e.type ?? 'references' })),
          filterName,
        }),
        signal: controller.signal,
      })

      if (!res.ok) {
        const body = await res.text()
        throw new Error(`Compile failed: ${res.status} ${body}`)
      }

      // Read SSE stream
      const reader = res.body?.getReader()
      if (!reader) throw new Error('No response body')
      const decoder = new TextDecoder()
      let buffer = ''

      while (true) {
        const { done, value } = await reader.read()
        if (done) break

        buffer += decoder.decode(value, { stream: true })
        const lines = buffer.split('\n')
        buffer = lines.pop() ?? ''

        let eventType = ''
        let eventData = ''
        for (const line of lines) {
          if (line.startsWith('event: ')) {
            eventType = line.slice(7)
          } else if (line.startsWith('data: ')) {
            eventData = line.slice(6)
          } else if (line === '' && eventType && eventData) {
            try {
              const data = JSON.parse(eventData)
              handleSSEEvent(eventType, data)
            } catch { /* skip malformed */ }
            eventType = ''
            eventData = ''
          }
        }
      }
    } catch (err) {
      if (!abortRef.current) {
        setCompileError(err instanceof Error ? err.message : 'Compile failed')
      }
    } finally {
      setCompiling(false)
      eventSourceRef.current = null
      loadHistory()
    }
  }, [snapshot, filterName, getAccessToken, loadHistory])

  function handleSSEEvent(event: string, data: Record<string, unknown>) {
    if (event === 'start') {
      setCompileId(data.compileId as string)
    } else if (event === 'progress') {
      const stepNum = data.step as number
      const status = data.status as string
      const detail = data.detail as string | undefined
      const label = data.label as string
      setSteps((prev) => prev.map((s) =>
        s.step === stepNum ? { ...s, label, status: status as CompileStep['status'], detail } : s
      ))
    } else if (event === 'complete') {
      setCompileResult(data)
    } else if (event === 'error') {
      setCompileError(data.error as string)
    } else if (event === 'aborted') {
      setCompileError('Compile aborted')
    }
  }

  const handleAbort = useCallback(() => {
    abortRef.current = true
    eventSourceRef.current?.abort()
    if (compileId) {
      const token = getAccessToken()
      if (token) {
        fetch(proxyUrl(SERVICE, `/compile-stream/${compileId}/abort`), {
          method: 'POST',
          headers: { Authorization: `Bearer ${token}` },
        }).catch(() => {})
      }
    }
  }, [compileId, getAccessToken])

  const handleDownload = useCallback(async (id: string, type: 'latex' | 'pdf') => {
    const token = getAccessToken()
    if (!token) return
    try {
      const res = await fetch(proxyUrl(SERVICE, `/compile-history/${id}/download/${type}`), {
        headers: { Authorization: `Bearer ${token}` },
      })
      if (!res.ok) throw new Error(`Download failed: ${res.status}`)
      const data = await res.json()
      if (data.url) {
        window.open(data.url, '_blank')
      }
    } catch { /* ignore */ }
  }, [getAccessToken])

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center" style={{ background: 'rgba(0,0,0,0.7)', backdropFilter: 'blur(6px)' }}>
      <div className="rounded-lg overflow-hidden flex" style={{
        background: 'var(--bg-base)', border: '1px solid var(--border-default)',
        width: '85vw', maxWidth: 1100, height: '70vh', maxHeight: 650,
      }}>
        {/* Left: Compile Progress */}
        <div className="flex-1 flex flex-col" style={{ borderRight: '1px solid var(--border-default)' }}>
          <div className="flex items-center justify-between px-5 py-3 shrink-0" style={{ borderBottom: '1px solid var(--border-default)' }}>
            <span className="text-sm font-semibold" style={{ color: 'var(--text-primary)' }}>Compile Paper</span>
            <button onClick={onClose} className="icon-btn"><X size={16} /></button>
          </div>

          <div className="flex-1 overflow-auto p-5 space-y-3">
            {/* Action button */}
            <div className="flex items-center gap-3 mb-4">
              {!compiling ? (
                <button onClick={handleStart} disabled={!snapshot || snapshot.nodes.length === 0}
                  className="btn-neon-green text-xs gap-1.5 py-2 px-4 disabled:opacity-30">
                  <Play size={14} /> Start Compile ({snapshot?.nodes.length ?? 0} nodes, {snapshot?.edges.length ?? 0} edges)
                </button>
              ) : (
                <button onClick={handleAbort} className="btn-neon-danger text-xs gap-1.5 py-2 px-4">
                  <Square size={14} /> Abort
                </button>
              )}
              {compileError && (
                <span className="text-[11px] truncate" style={{ color: 'var(--accent-red)' }}>{compileError}</span>
              )}
            </div>

            {/* Progress steps */}
            <div className="space-y-2">
              {steps.map((s) => (
                <div key={s.step} className="flex items-start gap-3 py-1.5">
                  <div className="w-5 h-5 shrink-0 flex items-center justify-center mt-0.5">
                    {s.status === 'running' && <Loader2 size={14} className="animate-spin" style={{ color: 'var(--neon-cyan)' }} />}
                    {s.status === 'done' && <div className="w-3 h-3 rounded-full" style={{ background: 'var(--neon-green)' }} />}
                    {s.status === 'error' && <div className="w-3 h-3 rounded-full" style={{ background: 'var(--neon-red)' }} />}
                    {s.status === 'pending' && <div className="w-3 h-3 rounded-full" style={{ background: 'var(--border-default)' }} />}
                  </div>
                  <div className="flex-1 min-w-0">
                    <div className="text-xs font-medium" style={{
                      color: s.status === 'done' ? 'var(--neon-green)' :
                             s.status === 'running' ? 'var(--neon-cyan)' :
                             s.status === 'error' ? 'var(--neon-red)' : 'var(--text-dimmed)'
                    }}>
                      {s.label}
                    </div>
                    {s.detail && <div className="text-[10px] mt-0.5" style={{ color: 'var(--text-dimmed)' }}>{s.detail}</div>}
                  </div>
                </div>
              ))}
            </div>

            {/* Complete result */}
            {compileResult && (
              <div className="mt-4 p-3 rounded" style={{ background: 'rgba(0,255,136,0.08)', border: '1px solid rgba(0,255,136,0.2)' }}>
                <div className="text-xs font-semibold mb-1" style={{ color: 'var(--neon-green)' }}>Compile Complete</div>
                <div className="text-[10px] space-y-0.5" style={{ color: 'var(--text-secondary)' }}>
                  <div>PDF: {compileResult.pdfFileName as string} ({((compileResult.pdfSize as number) / 1024).toFixed(0)} KB)</div>
                  <div>LaTeX: {compileResult.latexFileName as string} ({((compileResult.latexSize as number) / 1024).toFixed(0)} KB)</div>
                </div>
              </div>
            )}
          </div>
        </div>

        {/* Right: History */}
        <div className="flex flex-col" style={{ width: 420 }}>
          <div className="flex items-center justify-between px-4 py-3 shrink-0" style={{ borderBottom: '1px solid var(--border-default)' }}>
            <span className="text-[11px] font-semibold uppercase tracking-wider" style={{ color: 'var(--text-dimmed)' }}>
              Compile History
            </span>
            {historyLoading && <Loader2 size={12} className="animate-spin" style={{ color: 'var(--text-dimmed)' }} />}
          </div>
          <div className="flex-1 overflow-auto">
            {history.length === 0 && !historyLoading && (
              <div className="p-4 text-center text-[11px]" style={{ color: 'var(--text-dimmed)' }}>No previous compiles</div>
            )}
            {history.map((h) => (
              <div key={h.id} className="px-4 py-2.5 hover:bg-[rgba(125,211,252,0.03)]" style={{ borderBottom: '1px solid var(--border-subtle)' }}>
                <div className="flex items-center justify-between mb-1">
                  <span className="text-[11px] font-medium truncate" style={{ color: 'var(--text-primary)' }}>
                    {h.filterName}
                  </span>
                  <span className="text-[9px] px-1.5 py-0.5 rounded shrink-0" style={{
                    background: h.status === 'completed' ? 'rgba(0,255,136,0.1)' : h.status === 'failed' ? 'rgba(255,68,68,0.1)' : 'rgba(125,211,252,0.1)',
                    color: h.status === 'completed' ? 'var(--neon-green)' : h.status === 'failed' ? 'var(--neon-red)' : 'var(--neon-cyan)',
                  }}>
                    {h.status}
                  </span>
                </div>
                <div className="text-[10px] mb-1.5" style={{ color: 'var(--text-dimmed)' }}>
                  {h.nodeCount}n / {h.edgeCount}e — {new Date(h.startedAt).toLocaleString()}
                </div>
                {h.status === 'completed' && (
                  <div className="flex items-center gap-2">
                    {h.pdfFileName && (
                      <button onClick={() => handleDownload(h.id, 'pdf')}
                        className="flex items-center gap-1 text-[10px] px-2 py-0.5 rounded hover:opacity-80"
                        style={{ color: 'var(--neon-cyan)', border: '1px solid rgba(125,211,252,0.2)' }}>
                        <File size={10} /> PDF
                      </button>
                    )}
                    {h.latexFileName && (
                      <button onClick={() => handleDownload(h.id, 'latex')}
                        className="flex items-center gap-1 text-[10px] px-2 py-0.5 rounded hover:opacity-80"
                        style={{ color: 'var(--neon-cyan)', border: '1px solid rgba(125,211,252,0.2)' }}>
                        <FileText size={10} /> LaTeX
                      </button>
                    )}
                  </div>
                )}
                {h.error && <div className="text-[10px] mt-1 truncate" style={{ color: 'var(--accent-red)' }}>{h.error}</div>}
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  )
}
