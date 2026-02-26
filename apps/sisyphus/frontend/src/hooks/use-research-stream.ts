import { useState, useCallback, useRef } from 'react'
import { startResearchStream } from '../api'
import type { AgentMessage, ToolCall, TimelineItem, RunStatus, SSEEvent } from '../types'

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
          setMessages((prev) =>
            prev.map((m) =>
              m.id === event.messageId
                ? { ...m, content: m.content + event.delta }
                : m,
            ),
          )
        }
        break

      case 'TEXT_MESSAGE_END':
        if (event.messageId) {
          setMessages((prev) =>
            prev.map((m) =>
              m.id === event.messageId ? { ...m, isStreaming: false } : m,
            ),
          )
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
  }, [])

  const startRun = useCallback(
    (prompt: string, workflow?: string, agentId?: string) => {
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
