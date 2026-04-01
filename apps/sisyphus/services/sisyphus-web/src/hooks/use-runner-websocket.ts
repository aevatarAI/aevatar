import { useState, useCallback, useRef, useEffect } from 'react'
import { subscribeRunEvents } from '../api/runner-api'
import { useAuth } from '../auth/useAuth'

export interface RunnerEvent {
  type: string
  step_name?: string
  data?: Record<string, unknown>
  error?: string
  timestamp?: string
}

export type RunnerStatus = 'idle' | 'running' | 'completed' | 'failed' | 'stopped'

const MIN_RECONNECT_DELAY_MS = 1_000
const MAX_RECONNECT_DELAY_MS = 30_000
const BACKOFF_MULTIPLIER = 2

export function useRunnerWebSocket() {
  const { getAccessToken } = useAuth()
  const [events, setEvents] = useState<RunnerEvent[]>([])
  const [status, setStatus] = useState<RunnerStatus>('idle')
  const [error, setError] = useState<string | null>(null)
  const [reconnecting, setReconnecting] = useState(false)

  const abortRef = useRef<AbortController | null>(null)
  const runIdRef = useRef<string | null>(null)
  const reconnectTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const reconnectDelayRef = useRef(MIN_RECONNECT_DELAY_MS)
  /** Whether the workflow has reached a terminal state (no reconnect needed) */
  const terminalRef = useRef(false)
  /** Whether the user explicitly disconnected */
  const manualDisconnectRef = useRef(false)

  const clearReconnectTimer = useCallback(() => {
    if (reconnectTimerRef.current) {
      clearTimeout(reconnectTimerRef.current)
      reconnectTimerRef.current = null
    }
  }, [])

  const connectToStream = useCallback(
    (runId: string) => {
      const token = getAccessToken()
      if (!token) {
        setError('Not authenticated')
        return
      }

      const abort = new AbortController()
      abortRef.current = abort
      setReconnecting(false)

      subscribeRunEvents(
        runId,
        token,
        (event) => {
          // Successful data means connection is healthy — reset backoff
          reconnectDelayRef.current = MIN_RECONNECT_DELAY_MS

          const runnerEvent: RunnerEvent = {
            type: event.type as string,
            step_name: event.step_name as string | undefined,
            data: event.data as Record<string, unknown> | undefined,
            error: event.error as string | undefined,
            timestamp: (event.timestamp as string | undefined) ?? new Date().toISOString(),
          }

          setEvents((prev) => [...prev, runnerEvent])

          if (event.type === 'WorkflowCompleted') {
            terminalRef.current = true
            setStatus('completed')
          } else if (event.type === 'WorkflowFailed') {
            terminalRef.current = true
            setStatus('failed')
            setError((event.error as string) ?? 'Workflow failed')
          } else if (event.type === 'WorkflowStopped') {
            terminalRef.current = true
            setStatus('stopped')
          }
        },
        () => {
          // Stream ended (onDone)
          abortRef.current = null

          if (terminalRef.current || manualDisconnectRef.current) {
            // Workflow finished or user disconnected — no reconnect
            setStatus((prev) => (prev === 'running' ? 'completed' : prev))
            return
          }

          // Unexpected stream end while still running — schedule reconnect
          scheduleReconnect(runId)
        },
        (err) => {
          abortRef.current = null

          if (terminalRef.current || manualDisconnectRef.current) {
            setStatus((prev) => (prev === 'running' ? 'failed' : prev))
            setError(err.message)
            return
          }

          // Connection error while running — schedule reconnect
          console.warn(`SSE connection error, will retry: ${err.message}`)
          scheduleReconnect(runId)
        },
        abort.signal,
      )
    },
    [getAccessToken],
  )

  const scheduleReconnect = useCallback(
    (runId: string) => {
      const delay = reconnectDelayRef.current
      console.info(`Reconnecting to run ${runId} in ${delay}ms`)
      setReconnecting(true)

      reconnectTimerRef.current = setTimeout(() => {
        reconnectTimerRef.current = null
        // Increase delay for next attempt (exponential backoff)
        reconnectDelayRef.current = Math.min(delay * BACKOFF_MULTIPLIER, MAX_RECONNECT_DELAY_MS)
        connectToStream(runId)
      }, delay)
    },
    [connectToStream],
  )

  const subscribe = useCallback(
    (runId: string) => {
      // Clean up any existing connection
      clearReconnectTimer()
      abortRef.current?.abort()

      runIdRef.current = runId
      terminalRef.current = false
      manualDisconnectRef.current = false
      reconnectDelayRef.current = MIN_RECONNECT_DELAY_MS

      setEvents([])
      setStatus('running')
      setError(null)
      setReconnecting(false)

      connectToStream(runId)
    },
    [connectToStream, clearReconnectTimer],
  )

  const disconnect = useCallback(() => {
    manualDisconnectRef.current = true
    clearReconnectTimer()
    abortRef.current?.abort()
    abortRef.current = null
    runIdRef.current = null
    setReconnecting(false)
  }, [clearReconnectTimer])

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      manualDisconnectRef.current = true
      if (reconnectTimerRef.current) clearTimeout(reconnectTimerRef.current)
      abortRef.current?.abort()
    }
  }, [])

  return { events, status, error, reconnecting, subscribe, disconnect }
}
