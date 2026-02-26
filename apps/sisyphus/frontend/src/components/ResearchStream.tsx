import { useEffect, useMemo, useRef, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { Bot, Wrench, AlertCircle, ChevronRight, ChevronDown } from 'lucide-react'
import clsx from 'clsx'
import type { AgentMessage, ToolCall, TimelineItem, RunStatus } from '../types'

const ROLE_COLORS: Record<string, { border: string; label: string; text: string }> = {
  researcher: { border: 'var(--accent-blue)', label: 'Researcher', text: 'var(--accent-blue)' },
  verifier: { border: 'var(--accent-purple)', label: 'Verifier', text: 'var(--accent-purple)' },
  dag_builder: { border: 'var(--accent-gold)', label: 'DAG Builder', text: 'var(--accent-gold)' },
}

function getRoleStyle(role: string) {
  const key = role.toLowerCase().replace(/\s+/g, '_')
  return ROLE_COLORS[key] ?? { border: 'var(--text-muted)', label: role, text: 'var(--text-muted)' }
}

function formatDuration(start: number, end?: number): string {
  const ms = (end ?? Date.now()) - start
  if (ms < 1000) return `${ms}ms`
  return `${(ms / 1000).toFixed(1)}s`
}

function formatJson(str: string): string {
  try {
    return JSON.stringify(JSON.parse(str), null, 2)
  } catch {
    return str
  }
}

/** For meta-tools like nyx_call_tool, extract the underlying service tool name from args. */
function getUnderlyingToolName(toolName: string, args?: string): string | null {
  if (!args) return null
  if (toolName === 'nyx_call_tool') {
    try {
      const parsed = JSON.parse(args)
      return parsed.tool_name ?? null
    } catch {
      return null
    }
  }
  if (toolName === 'nyx_search_tools') {
    try {
      const parsed = JSON.parse(args)
      return parsed.query ?? null
    } catch {
      return null
    }
  }
  return null
}

/** Walk backwards through the timeline to find the role from the nearest preceding step. */
function resolveRoleFromTimeline(timeline: TimelineItem[], itemId: string): string {
  let found = false
  for (let i = timeline.length - 1; i >= 0; i--) {
    const t = timeline[i]
    if (!found) {
      if ((t.type === 'message' && t.id === itemId) || (t.type === 'tool' && t.id === itemId)) {
        found = true
      }
      continue
    }
    if (t.type === 'step') return t.role
  }
  return 'researcher'
}

type IterationStep = { childId: string; role: string; items: TimelineItem[] }

type RenderSection =
  | { kind: 'flat'; items: TimelineItem[] }
  | { kind: 'iteration'; iteration: number; steps: IterationStep[] }

function buildSections(timeline: TimelineItem[]): RenderSection[] {
  const sections: RenderSection[] = []
  let currentFlat: TimelineItem[] | null = null
  let currentIter: { iteration: number; steps: IterationStep[] } | null = null
  let currentStep: IterationStep | null = null

  for (const item of timeline) {
    if (item.type === 'step') {
      if (item.iteration !== undefined) {
        if (currentFlat) {
          sections.push({ kind: 'flat', items: currentFlat })
          currentFlat = null
        }
        if (!currentIter || currentIter.iteration !== item.iteration) {
          if (currentIter) {
            if (currentStep) {
              currentIter.steps.push(currentStep)
              currentStep = null
            }
            sections.push({ kind: 'iteration', ...currentIter })
          }
          currentIter = { iteration: item.iteration, steps: [] }
        }
        if (currentStep) {
          currentIter.steps.push(currentStep)
        }
        currentStep = { childId: item.childId ?? item.name, role: item.role, items: [] }
      } else {
        if (currentIter) {
          if (currentStep) {
            currentIter.steps.push(currentStep)
            currentStep = null
          }
          sections.push({ kind: 'iteration', ...currentIter })
          currentIter = null
        }
        if (!currentFlat) currentFlat = []
        currentFlat.push(item)
      }
    } else {
      if (currentStep) {
        currentStep.items.push(item)
      } else if (currentFlat) {
        currentFlat.push(item)
      } else {
        currentFlat = [item]
      }
    }
  }

  if (currentIter) {
    if (currentStep) currentIter.steps.push(currentStep)
    sections.push({ kind: 'iteration', ...currentIter })
  }
  if (currentFlat) {
    sections.push({ kind: 'flat', items: currentFlat })
  }

  return sections
}

function summarizeIteration(steps: IterationStep[]): string {
  const toolCount = steps.reduce(
    (sum, s) => sum + s.items.filter((i) => i.type === 'tool').length,
    0,
  )
  return `${steps.length} step${steps.length !== 1 ? 's' : ''}, ${toolCount} tool call${toolCount !== 1 ? 's' : ''}`
}

const CHILD_LABELS: Record<string, string> = {
  verify: 'Verifier',
  build_dag: 'DAG Builder',
  next_round: 'Researcher',
}

interface ResearchStreamProps {
  messages: AgentMessage[]
  toolCalls: ToolCall[]
  timeline: TimelineItem[]
  runStatus: RunStatus
  currentStep: string | null
  error: string | null
  iterationCount: number
}

export default function ResearchStream({
  messages,
  toolCalls,
  timeline,
  runStatus,
  currentStep,
  error,
  iterationCount,
}: ResearchStreamProps) {
  const bottomRef = useRef<HTMLDivElement>(null)
  const [expandedTools, setExpandedTools] = useState<Set<string>>(new Set())
  const [collapsedIterations, setCollapsedIterations] = useState<Set<number>>(new Set())
  const prevIterationCountRef = useRef(0)

  const toggleTool = (id: string) => {
    setExpandedTools((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  const toggleIteration = (iter: number) => {
    setCollapsedIterations((prev) => {
      const next = new Set(prev)
      if (next.has(iter)) next.delete(iter)
      else next.add(iter)
      return next
    })
  }

  // Auto-collapse completed iterations when a new one starts
  useEffect(() => {
    if (iterationCount > prevIterationCountRef.current && iterationCount > 1) {
      setCollapsedIterations((prev) => {
        const next = new Set(prev)
        for (let i = 0; i < iterationCount - 1; i++) {
          next.add(i)
        }
        return next
      })
    }
    prevIterationCountRef.current = iterationCount
  }, [iterationCount])

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages, toolCalls, timeline])

  const sections = useMemo(() => buildSections(timeline), [timeline])

  if (runStatus === 'idle') {
    return (
      <div className="h-full flex items-center justify-center">
        <div className="text-center space-y-3">
          <div className="font-display text-lg font-semibold" style={{ color: 'var(--text-dimmed)' }}>
            awaiting input
          </div>
          <p className="text-[13px]" style={{ color: 'var(--text-dimmed)' }}>
            Enter a research topic below to begin
          </p>
        </div>
      </div>
    )
  }

  function renderToolCall(item: TimelineItem & { type: 'tool' }) {
    const tc = toolCalls.find((t) => t.id === item.id)
    if (!tc) return null
    const isExpanded = expandedTools.has(tc.id)
    const hasDetails = tc.args || tc.result
    const isRunning = tc.status === 'running'

    // For nyx_call_tool, extract the underlying service tool name from args
    const underlyingTool = getUnderlyingToolName(tc.name, tc.args)

    return (
      <div
        key={`tool-${item.id}`}
        className={clsx('animate-fade-in rounded-md min-w-0', hasDetails && 'cursor-pointer')}
        style={{ background: 'var(--bg-elevated)' }}
        onClick={() => hasDetails && toggleTool(tc.id)}
      >
        <div className="flex items-center gap-3 px-3 py-2 min-w-0">
          {hasDetails ? (
            isExpanded
              ? <ChevronDown size={14} className="shrink-0" style={{ color: 'var(--text-dimmed)' }} />
              : <ChevronRight size={14} className="shrink-0" style={{ color: 'var(--text-dimmed)' }} />
          ) : (
            <Wrench size={14} className="shrink-0" style={{ color: 'var(--text-muted)' }} />
          )}
          <div className="flex items-center gap-2 min-w-0 flex-1">
            <span className="font-mono text-xs font-semibold truncate" style={{ color: 'var(--text-secondary)' }}>
              {tc.name}
            </span>
            {underlyingTool && (
              <span className="font-mono text-[10px] truncate" style={{ color: 'var(--text-dimmed)' }}>
                → {underlyingTool}
              </span>
            )}
          </div>
          <span className={clsx('status-dot shrink-0', isRunning ? 'status-dot-running' : tc.status === 'completed' ? 'status-dot-active' : 'status-dot-error')} />
          {isRunning ? (
            <span className="text-xs font-mono shrink-0 flex items-center gap-1.5" style={{ color: 'var(--accent-gold)' }}>
              running
              <span className="thinking-dots" style={{ '--dot-color': 'var(--accent-gold)' } as React.CSSProperties}>
                <span />
                <span />
                <span />
              </span>
            </span>
          ) : (
            <span className="text-xs font-mono shrink-0" style={{ color: 'var(--text-dimmed)' }}>
              {formatDuration(tc.startTime, tc.endTime)}
            </span>
          )}
        </div>
        {isExpanded && (
          <div className="px-3 pb-3 space-y-2 overflow-hidden" onClick={(e) => e.stopPropagation()}>
            {tc.args && (
              <div className="min-w-0">
                <div className="text-[10px] font-semibold uppercase tracking-wider mb-1" style={{ color: 'var(--text-muted)' }}>
                  Arguments
                </div>
                <pre
                  className="text-[11px] font-mono p-2 rounded overflow-auto max-h-48 whitespace-pre-wrap break-all"
                  style={{ background: 'var(--bg-base)', color: 'var(--text-secondary)' }}
                >{formatJson(tc.args)}</pre>
              </div>
            )}
            {tc.result && (
              <div className="min-w-0">
                <div className="text-[10px] font-semibold uppercase tracking-wider mb-1" style={{ color: 'var(--accent-green)' }}>
                  Result
                </div>
                <pre
                  className="text-[11px] font-mono p-2 rounded overflow-auto max-h-48 whitespace-pre-wrap break-all"
                  style={{ background: 'var(--bg-base)', color: 'var(--text-secondary)' }}
                >{formatJson(tc.result)}</pre>
              </div>
            )}
          </div>
        )}
      </div>
    )
  }

  function renderMessage(item: TimelineItem & { type: 'message' }, opts?: { role?: string; iteration?: number; hideHeader?: boolean }) {
    const msg = messages.find((m) => m.id === item.id)
    if (!msg) return null
    const resolvedRole = opts?.role ?? resolveRoleFromTimeline(timeline, item.id)
    const style = getRoleStyle(resolvedRole)
    const iterLabel = opts?.iteration !== undefined ? ` (iter ${opts.iteration + 1})` : ''
    return (
      <div key={`msg-${item.id}`} className="animate-fade-in">
        {!opts?.hideHeader && (
          <div className="flex items-center gap-2 mb-1.5">
            <Bot size={14} style={{ color: style.text }} />
            <span className="text-xs font-semibold font-mono uppercase tracking-wider" style={{ color: style.text }}>
              {style.label}{iterLabel}
            </span>
          </div>
        )}
        <div
          className="chat-bubble agent min-w-0 overflow-hidden"
          style={{ borderLeftColor: style.border }}
        >
          <div className="prose-sm min-w-0 overflow-hidden">
            <ReactMarkdown remarkPlugins={[remarkGfm]}>
              {msg.content || ' '}
            </ReactMarkdown>
            {msg.isStreaming && (
              <div className="inline-flex items-center gap-1 ml-1 align-middle">
                <div className="thinking-dots">
                  <span />
                  <span />
                  <span />
                </div>
              </div>
            )}
          </div>
        </div>
      </div>
    )
  }

  function renderItem(item: TimelineItem, idx: number) {
    if (item.type === 'step') {
      const style = getRoleStyle(item.role)
      const iterLabel = item.iteration !== undefined ? ` (iter ${item.iteration + 1})` : ''
      return (
        <div key={`step-${idx}`} className="flex items-center gap-3 py-2">
          <div className="flex-1 h-px" style={{ background: 'var(--border-default)' }} />
          <span
            className="text-[10px] font-semibold font-mono uppercase tracking-wider px-2 py-0.5 rounded"
            style={{ color: style.text, background: 'var(--bg-accent)' }}
          >
            {style.label}{iterLabel} — {item.name}
          </span>
          <div className="flex-1 h-px" style={{ background: 'var(--border-default)' }} />
        </div>
      )
    }
    if (item.type === 'message') return renderMessage(item)
    if (item.type === 'tool') return renderToolCall(item)
    return null
  }

  function renderChildStep(step: IterationStep, stepIdx: number, totalSteps: number, iteration: number) {
    const roleStyle = getRoleStyle(step.role)
    const label = CHILD_LABELS[step.childId] ?? step.childId
    const isFirst = stepIdx === 0
    const isLast = stepIdx === totalSteps - 1

    return (
      <div key={`child-${step.childId}-${stepIdx}`} className="relative pl-5">
        {/* Vertical connector line */}
        <div
          className="absolute left-[9px] w-px"
          style={{
            background: 'var(--border-default)',
            top: isFirst ? '12px' : 0,
            bottom: isLast ? 'calc(100% - 12px)' : 0,
          }}
        />
        {/* Branch connector */}
        <div className="absolute left-[9px] top-[12px] w-[10px] h-px" style={{ background: 'var(--border-default)' }} />
        {/* Dot on the connector */}
        <div
          className="absolute left-[6px] top-[9px] w-[7px] h-[7px] rounded-full"
          style={{ background: roleStyle.border, border: '2px solid var(--bg-surface)' }}
        />

        <div className="pb-3">
          <div className="flex items-center gap-2 mb-2 ml-2">
            <span
              className="text-[10px] font-semibold font-mono uppercase tracking-wider px-1.5 py-0.5 rounded"
              style={{ color: roleStyle.text, background: 'var(--bg-accent)' }}
            >
              {label}
            </span>
          </div>
          <div className="space-y-2 ml-2 min-w-0">
            {step.items.map((item, i) => {
              if (item.type === 'tool') return renderToolCall(item)
              if (item.type === 'message') return renderMessage(item, { role: step.role, iteration, hideHeader: true })
              return <div key={`item-${i}`} />
            })}
            {step.items.length === 0 && (
              <div className="text-[10px] font-mono py-1" style={{ color: 'var(--text-dimmed)' }}>
                awaiting...
              </div>
            )}
          </div>
        </div>
      </div>
    )
  }

  function renderIteration(section: RenderSection & { kind: 'iteration' }) {
    const isCollapsed = collapsedIterations.has(section.iteration)
    const isCurrent = section.iteration === iterationCount - 1 && runStatus === 'running'

    return (
      <div key={`iter-${section.iteration}`} className="animate-fade-in">
        {/* Iteration header */}
        <button
          onClick={() => toggleIteration(section.iteration)}
          className="w-full flex items-center gap-3 py-2 px-3 rounded-md transition-colors"
          style={{
            background: isCurrent
              ? 'var(--bg-accent)'
              : 'var(--bg-elevated)',
          }}
        >
          {isCollapsed
            ? <ChevronRight size={14} style={{ color: 'var(--text-dimmed)' }} />
            : <ChevronDown size={14} style={{ color: 'var(--text-dimmed)' }} />
          }
          <div className="h-px flex-1" style={{ background: 'var(--border-default)' }} />
          <span
            className="text-[10px] font-display font-semibold uppercase tracking-[0.15em]"
            style={{ color: isCurrent ? 'var(--text-primary)' : 'var(--text-muted)' }}
          >
            Iteration {section.iteration + 1}
          </span>
          {isCurrent && (
            <span className="status-dot status-dot-running" />
          )}
          <div className="h-px flex-1" style={{ background: 'var(--border-default)' }} />
          {isCollapsed && (
            <span className="text-[10px] font-mono" style={{ color: 'var(--text-dimmed)' }}>
              {summarizeIteration(section.steps)}
            </span>
          )}
        </button>

        {/* Iteration body */}
        {!isCollapsed && (
          <div className="mt-2 mb-4">
            {section.steps.map((step, i) =>
              renderChildStep(step, i, section.steps.length, section.iteration),
            )}
          </div>
        )}
      </div>
    )
  }

  return (
    <div className="h-full flex flex-col min-w-0 overflow-hidden">
      <div className="flex items-center justify-between px-4 py-3 min-w-0">
        <h2 className="text-[13px] font-semibold" style={{ color: 'var(--text-muted)' }}>
          Research Output
        </h2>
        <div className="flex items-center gap-3">
          {iterationCount > 0 && (
            <span className="text-[10px] font-mono" style={{ color: 'var(--text-dimmed)' }}>
              {iterationCount} iteration{iterationCount !== 1 ? 's' : ''}
            </span>
          )}
          {currentStep && (
            <span className="badge text-[10px]">
              {currentStep}
            </span>
          )}
        </div>
      </div>
      <div className="divider-h" />
      <div className="flex-1 overflow-auto p-4 space-y-3 min-w-0">
        {sections.map((section, sIdx) => {
          if (section.kind === 'flat') {
            return (
              <div key={`flat-${sIdx}`} className="space-y-3">
                {section.items.map((item, i) => renderItem(item, sIdx * 1000 + i))}
              </div>
            )
          }
          return renderIteration(section)
        })}

        {error && (
          <div
            className="flex items-center gap-2 px-3 py-2 rounded-md"
            style={{ background: 'rgba(255,46,46,0.08)', border: '1px solid rgba(255,46,46,0.2)' }}
          >
            <AlertCircle size={14} style={{ color: 'var(--accent-red)' }} />
            <span className="text-xs" style={{ color: 'var(--accent-red)' }}>{error}</span>
          </div>
        )}

        <div ref={bottomRef} />
      </div>
    </div>
  )
}
