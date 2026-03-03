import { useState, useCallback, useRef, useEffect } from 'react'
import { startResearchStream } from '../api'
import type { AgentMessage, ToolCall, TimelineItem, RunStatus, SSEEvent } from '../types'

/**
 * Typewriter delta queue — buffers incoming text deltas and drains them
 * at a controlled pace via requestAnimationFrame so React renders each
 * small batch visibly instead of batching them all into one frame.
 *
 * CHARS_PER_FRAME controls the visible typing speed:
 *   4 chars/frame × 60 fps = 240 chars/sec — visible typing effect.
 * When a message ends, drain speeds up (FLUSH_CHARS_PER_FRAME) to avoid
 * stale text sitting in the queue.
 */
const CHARS_PER_FRAME = 4
const FLUSH_CHARS_PER_FRAME = 80

function deriveRole(stepName: string): string {
  const s = stepName.toLowerCase()
  if (s.includes('verify') || s.includes('verifier')) return 'verifier'
  if (s.includes('build_dag') || s.includes('dag')) return 'dag_builder'
  return 'researcher'
}

const ITER_RE = /iter_(\d+)(?:_(.+))?$/

function parseStepContext(stepName: string): { iteration?: number; childId?: string; isLoop: boolean } {
  const match = stepName.match(ITER_RE)
  if (!match) return { iteration: undefined, childId: undefined, isLoop: false }
  return {
    iteration: parseInt(match[1], 10),
    childId: match[2] ?? undefined,
    isLoop: true,
  }
}

/** Extract the step name suffix from a messageId like "msg:Workflow:005fc55e:research_loop_iter_0_verify" */
function extractStepFromMessageId(messageId: string): string | null {
  const parts = messageId.split(':')
  return parts.length >= 4 ? parts.slice(3).join(':') : null
}

export function useResearchStream() {
  const [messages, setMessages] = useState<AgentMessage[]>([])
  const [toolCalls, setToolCalls] = useState<ToolCall[]>([])
  const [timeline, setTimeline] = useState<TimelineItem[]>([])
  const [runStatus, setRunStatus] = useState<RunStatus>('idle')
  const [currentStep, setCurrentStep] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [iterationCount, setIterationCount] = useState(0)
  const abortRef = useRef<AbortController | null>(null)
  const currentRoleRef = useRef<string>('researcher')
  const seenStepsRef = useRef<Set<string>>(new Set())

  // ─── Typewriter delta queue ───
  // Keyed by messageId → pending text to reveal
  const deltaQueueRef = useRef<Map<string, string>>(new Map())
  const rafRef = useRef<number | null>(null)
  // Track which messages have finished streaming (TEXT_MESSAGE_END received)
  // so we can drain them faster and mark isStreaming=false when their queue empties.
  const endedMessagesRef = useRef<Set<string>>(new Set())

  const drainDeltas = useCallback(() => {
    rafRef.current = null
    const queue = deltaQueueRef.current
    const ended = endedMessagesRef.current
    if (queue.size === 0) return

    // Build a batch: use faster drain rate for ended messages
    const batch = new Map<string, string>()
    const fullyDrained: string[] = []
    let hasMore = false
    for (const [id, pending] of queue) {
      const limit = ended.has(id) ? FLUSH_CHARS_PER_FRAME : CHARS_PER_FRAME
      if (pending.length <= limit) {
        batch.set(id, pending)
        queue.delete(id)
        if (ended.has(id)) fullyDrained.push(id)
      } else {
        batch.set(id, pending.slice(0, limit))
        queue.set(id, pending.slice(limit))
        hasMore = true
      }
    }

    if (batch.size > 0) {
      setMessages((prev) =>
        prev.map((m) => {
          const delta = batch.get(m.id)
          if (!delta) return m
          const updated = { ...m, content: m.content + delta }
          // Mark streaming complete once queue fully drained for ended messages
          if (fullyDrained.includes(m.id)) {
            updated.isStreaming = false
            ended.delete(m.id)
          }
          return updated
        }),
      )
    }

    if (hasMore || queue.size > 0) {
      rafRef.current = requestAnimationFrame(drainDeltas)
    }
  }, [])

  const enqueueDelta = useCallback(
    (messageId: string, delta: string) => {
      const queue = deltaQueueRef.current
      queue.set(messageId, (queue.get(messageId) ?? '') + delta)
      if (rafRef.current == null) {
        rafRef.current = requestAnimationFrame(drainDeltas)
      }
    },
    [drainDeltas],
  )

  // When a message stream ends, mark it for faster drain instead of instant flush.
  // If the queue is already empty for this message, mark isStreaming=false immediately.
  const markMessageEnded = useCallback((messageId: string) => {
    const queue = deltaQueueRef.current
    if (!queue.has(messageId)) {
      // Queue already empty — mark done immediately
      setMessages((prev) =>
        prev.map((m) => (m.id === messageId ? { ...m, isStreaming: false } : m)),
      )
    } else {
      // Queue still has pending text — let drainDeltas handle it at faster rate
      endedMessagesRef.current.add(messageId)
      if (rafRef.current == null) {
        rafRef.current = requestAnimationFrame(drainDeltas)
      }
    }
  }, [drainDeltas])

  // Cleanup raf on unmount
  useEffect(() => {
    return () => {
      if (rafRef.current != null) cancelAnimationFrame(rafRef.current)
    }
  }, [])

  const handleEvent = useCallback((event: SSEEvent) => {
    switch (event.type) {
      case 'RUN_STARTED':
        setRunStatus('running')
        break

      case 'RUN_FINISHED':
        setRunStatus('completed')
        break

      case 'RUN_ERROR':
        setRunStatus('error')
        setError(event.message ?? 'Unknown error')
        break

      case 'STEP_STARTED': {
        const stepName = event.stepName ?? ''
        setCurrentStep(stepName || null)
        const role = deriveRole(stepName)
        currentRoleRef.current = role
        if (stepName && !seenStepsRef.current.has(stepName)) {
          seenStepsRef.current.add(stepName)
          const ctx = parseStepContext(stepName)
          if (ctx.isLoop && ctx.iteration !== undefined) {
            setIterationCount((prev) => Math.max(prev, ctx.iteration! + 1))
          }
          setTimeline((prev) => [
            ...prev,
            { type: 'step', name: stepName, role, iteration: ctx.iteration, childId: ctx.childId },
          ])
        }
        break
      }

      case 'STEP_FINISHED':
        setCurrentStep(null)
        break

      case 'TEXT_MESSAGE_START':
        if (event.messageId) {
          // Derive role from the step name embedded in the messageId
          const stepFromMsg = extractStepFromMessageId(event.messageId)
          if (stepFromMsg) {
            const derivedRole = deriveRole(stepFromMsg)
            currentRoleRef.current = derivedRole

            // Synthesize step timeline entry since STEP_STARTED events may not arrive
            if (!seenStepsRef.current.has(stepFromMsg)) {
              seenStepsRef.current.add(stepFromMsg)
              const ctx = parseStepContext(stepFromMsg)
              if (ctx.isLoop && ctx.iteration !== undefined) {
                setIterationCount((prev) => Math.max(prev, ctx.iteration! + 1))
              }
              setCurrentStep(stepFromMsg)
              setTimeline((prev) => [
                ...prev,
                { type: 'step', name: stepFromMsg, role: derivedRole, iteration: ctx.iteration, childId: ctx.childId },
              ])
            }
          }

          const role = currentRoleRef.current
          setMessages((prev) => [
            ...prev,
            {
              id: event.messageId!,
              role,
              content: '',
              isStreaming: true,
            },
          ])
          setTimeline((prev) => [...prev, { type: 'message', id: event.messageId! }])
        }
        break

      case 'TEXT_MESSAGE_CONTENT':
        if (event.messageId && event.delta) {
          enqueueDelta(event.messageId, event.delta)
        }
        break

      case 'TEXT_MESSAGE_END':
        if (event.messageId) {
          // Let the typewriter drain remaining text at a faster rate,
          // then mark isStreaming=false when the queue empties.
          markMessageEnded(event.messageId)
        }
        break

      case 'TOOL_CALL_START':
        if (event.toolCallId && event.toolName) {
          setToolCalls((prev) => [
            ...prev,
            {
              id: event.toolCallId!,
              name: event.toolName!,
              args: event.args,
              status: 'running',
              startTime: event.timestamp ?? Date.now(),
            },
          ])
          setTimeline((prev) => [...prev, { type: 'tool', id: event.toolCallId! }])
        }
        break

      case 'TOOL_CALL_END':
        if (event.toolCallId) {
          const applyCompletion = () => {
            setToolCalls((prev) =>
              prev.map((tc) =>
                tc.id === event.toolCallId
                  ? {
                      ...tc,
                      status: 'completed' as const,
                      result: typeof event.result === 'string' ? event.result : undefined,
                      endTime: event.timestamp ?? Date.now(),
                    }
                  : tc,
              ),
            )
          }
          // Defer completion so the "running" (yellow) state is visible for at least ~400ms.
          // Without this, fast tool calls (< 50ms) have START and END batched into one render.
          requestAnimationFrame(() => setTimeout(applyCompletion, 1000))
        }
        break
    }
  }, [enqueueDelta, markMessageEnded])

  const startRun = useCallback(
    (prompt: string, workflow?: string, agentId?: string) => {
      if (rafRef.current != null) cancelAnimationFrame(rafRef.current)
      rafRef.current = null
      deltaQueueRef.current.clear()
      endedMessagesRef.current.clear()
      setMessages([])
      setToolCalls([])
      setTimeline([])
      setRunStatus('running')
      setCurrentStep(null)
      setError(null)
      setIterationCount(0)
      currentRoleRef.current = 'researcher'
      seenStepsRef.current = new Set()

      const abort = new AbortController()
      abortRef.current = abort

      startResearchStream(
        { prompt, workflow, agentId },
        handleEvent,
        () => {
          setRunStatus((prev) => (prev === 'running' ? 'completed' : prev))
          abortRef.current = null
        },
        (err) => {
          setRunStatus('error')
          setError(err.message)
          abortRef.current = null
        },
        abort.signal,
      )
    },
    [handleEvent],
  )

  const stopRun = useCallback(() => {
    abortRef.current?.abort()
    abortRef.current = null
    setRunStatus((prev) => (prev === 'running' ? 'completed' : prev))
  }, [])

  const clear = useCallback(() => {
    if (rafRef.current != null) cancelAnimationFrame(rafRef.current)
    rafRef.current = null
    deltaQueueRef.current.clear()
    endedMessagesRef.current.clear()
    setMessages([])
    setToolCalls([])
    setTimeline([])
    setRunStatus('idle')
    setCurrentStep(null)
    setError(null)
    setIterationCount(0)
  }, [])

  return { messages, toolCalls, timeline, runStatus, currentStep, error, iterationCount, startRun, stopRun, clear }
}
