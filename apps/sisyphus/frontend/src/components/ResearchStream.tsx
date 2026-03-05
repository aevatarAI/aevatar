import { useEffect, useRef, useState } from 'react'
import {
  ChevronRight,
  ChevronDown,
  CheckCircle2,
  AlertCircle,
  Loader2,
  BookOpen,
  Brain,
  Database,
  AlertTriangle,
} from 'lucide-react'
import type { RunStatus, RoundState, RoundEvent } from '../types'

interface ResearchStreamProps {
  rounds: RoundState[]
  runStatus: RunStatus
  currentRound: number
  totalBlueNodes: number
  error: string | null
}

const EVENT_ICONS: Record<string, typeof BookOpen> = {
  GRAPH_READ: BookOpen,
  LLM_CALL_START: Brain,
  LLM_CALL_DONE: Brain,
  GRAPH_WRITE_DONE: Database,
  VALIDATION_FAILED: AlertTriangle,
}

function eventLabel(event: RoundEvent): string {
  switch (event.type) {
    case 'ROUND_START':
      return `Round ${event.round} started`
    case 'GRAPH_READ':
      return `Read ${event.blue_node_count ?? 0} blue nodes from graph`
    case 'LLM_CALL_START':
      return 'LLM generating new nodes...'
    case 'LLM_CALL_DONE':
      return `LLM produced ${event.new_nodes ?? 0} nodes, ${event.new_edges ?? 0} edges`
    case 'VALIDATION_FAILED':
      return `Validation failed (attempt ${event.attempt}): ${event.errors?.join('; ') ?? 'unknown'}`
    case 'GRAPH_WRITE_DONE':
      return `Wrote ${event.nodes_written ?? 0} nodes, ${event.edges_written ?? 0} edges to graph`
    case 'ROUND_DONE':
      return `Round complete — ${event.total_blue_nodes ?? 0} total blue nodes`
    case 'LOOP_ERROR':
      return `Error: ${event.error ?? 'unknown'}`
    default:
      return event.type
  }
}

function eventColor(type: string): string {
  switch (type) {
    case 'GRAPH_READ':
      return 'var(--accent-blue)'
    case 'LLM_CALL_START':
      return 'var(--accent-gold)'
    case 'LLM_CALL_DONE':
      return 'var(--accent-green)'
    case 'GRAPH_WRITE_DONE':
      return 'var(--accent-purple)'
    case 'VALIDATION_FAILED':
    case 'LOOP_ERROR':
      return 'var(--accent-red)'
    default:
      return 'var(--text-dimmed)'
  }
}

function formatTime(ts: number): string {
  const d = new Date(ts)
  return d.toLocaleTimeString('en-US', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' })
}

function RoundCard({ round, isCurrent }: { round: RoundState; isCurrent: boolean }) {
  const [collapsed, setCollapsed] = useState(false)

  // Auto-collapse completed rounds
  useEffect(() => {
    if (round.status === 'done' && !isCurrent) {
      setCollapsed(true)
    }
  }, [round.status, isCurrent])

  const statusIcon =
    round.status === 'running' ? (
      <Loader2 size={14} className="animate-spin" style={{ color: 'var(--accent-gold)' }} />
    ) : round.status === 'error' ? (
      <AlertCircle size={14} style={{ color: 'var(--accent-red)' }} />
    ) : (
      <CheckCircle2 size={14} style={{ color: 'var(--accent-green)' }} />
    )

  const summaryParts: string[] = []
  if (round.nodesWritten !== undefined) summaryParts.push(`+${round.nodesWritten} nodes`)
  if (round.edgesWritten !== undefined) summaryParts.push(`+${round.edgesWritten} edges`)
  if (round.totalBlueNodes !== undefined) summaryParts.push(`${round.totalBlueNodes} total`)

  // Filter out ROUND_START and ROUND_DONE from event log
  const displayEvents = round.events.filter(
    (e) => e.type !== 'ROUND_START' && e.type !== 'ROUND_DONE',
  )

  return (
    <div className="animate-fade-in">
      <button
        onClick={() => setCollapsed(!collapsed)}
        className="w-full flex items-center gap-3 px-3 py-2 rounded-md transition-colors"
        style={{
          background: isCurrent ? 'var(--bg-accent)' : 'var(--bg-elevated)',
        }}
      >
        {collapsed ? (
          <ChevronRight size={14} style={{ color: 'var(--text-dimmed)' }} />
        ) : (
          <ChevronDown size={14} style={{ color: 'var(--text-dimmed)' }} />
        )}
        <div className="h-px flex-1" style={{ background: 'var(--border-default)' }} />
        {statusIcon}
        <span
          className="text-[10px] font-display font-semibold uppercase tracking-[0.15em]"
          style={{ color: isCurrent ? 'var(--text-primary)' : 'var(--text-muted)' }}
        >
          Round {round.round}
        </span>
        <div className="h-px flex-1" style={{ background: 'var(--border-default)' }} />
        {collapsed && summaryParts.length > 0 && (
          <span className="text-[10px] font-mono" style={{ color: 'var(--text-dimmed)' }}>
            {summaryParts.join(' · ')}
          </span>
        )}
      </button>

      {!collapsed && (
        <div className="mt-1 ml-4 mb-3 space-y-1">
          {displayEvents.map((event, i) => {
            const Icon = EVENT_ICONS[event.type] ?? BookOpen
            const color = eventColor(event.type)
            return (
              <div
                key={i}
                className="flex items-start gap-2 px-2 py-1.5 rounded"
                style={{ background: 'var(--bg-surface)' }}
              >
                <Icon size={12} className="shrink-0 mt-0.5" style={{ color }} />
                <span className="text-[11px] font-mono flex-1" style={{ color: 'var(--text-secondary)' }}>
                  {eventLabel(event)}
                </span>
                <span className="text-[10px] font-mono shrink-0" style={{ color: 'var(--text-dimmed)' }}>
                  {formatTime(event.timestamp)}
                </span>
              </div>
            )
          })}
          {round.status === 'running' && displayEvents.length > 0 && (
            <div className="flex items-center gap-2 px-2 py-1">
              <div className="thinking-dots" style={{ '--dot-color': 'var(--accent-gold)' } as React.CSSProperties}>
                <span />
                <span />
                <span />
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

export default function ResearchStream({
  rounds,
  runStatus,
  currentRound,
  totalBlueNodes,
  error,
}: ResearchStreamProps) {
  const bottomRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [rounds])

  if (runStatus === 'idle') {
    return (
      <div className="h-full flex items-center justify-center">
        <div className="text-center space-y-3">
          <div
            className="font-display text-lg font-semibold"
            style={{ color: 'var(--text-dimmed)' }}
          >
            awaiting start
          </div>
          <p className="text-[13px]" style={{ color: 'var(--text-dimmed)' }}>
            Start the research loop to grow the knowledge graph
          </p>
        </div>
      </div>
    )
  }

  return (
    <div className="h-full flex flex-col min-w-0 overflow-hidden">
      <div className="flex items-center justify-between px-4 py-3 min-w-0">
        <h2
          className="text-[13px] font-semibold"
          style={{ color: 'var(--text-muted)' }}
        >
          Research Loop
        </h2>
        <div className="flex items-center gap-3">
          {totalBlueNodes > 0 && (
            <span className="badge badge-blue text-[10px]">
              {totalBlueNodes} blue nodes
            </span>
          )}
          {currentRound > 0 && (
            <span className="text-[10px] font-mono" style={{ color: 'var(--text-dimmed)' }}>
              round {currentRound}
            </span>
          )}
        </div>
      </div>
      <div className="divider-h" />
      <div className="flex-1 overflow-auto p-4 space-y-2 min-w-0">
        {rounds.map((round) => (
          <RoundCard
            key={round.round}
            round={round}
            isCurrent={round.round === currentRound && runStatus === 'running'}
          />
        ))}

        {error && (
          <div
            className="flex items-center gap-2 px-3 py-2 rounded-md"
            style={{
              background: 'rgba(255,46,46,0.08)',
              border: '1px solid rgba(255,46,46,0.2)',
            }}
          >
            <AlertCircle size={14} style={{ color: 'var(--accent-red)' }} />
            <span className="text-xs" style={{ color: 'var(--accent-red)' }}>
              {error}
            </span>
          </div>
        )}

        <div ref={bottomRef} />
      </div>
    </div>
  )
}
