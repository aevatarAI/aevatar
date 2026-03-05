import { useState, useCallback, useRef, useEffect } from 'react'
import { startResearchV2, subscribeResearch, stopResearch, fetchResearchStatus } from '../api'
import type { ResearchV2Event } from '../api'
import type { RunStatus, RoundEvent, RoundState } from '../types'

export function useResearchStream() {
  const [rounds, setRounds] = useState<RoundState[]>([])
  const [runStatus, setRunStatus] = useState<RunStatus>('idle')
  const [currentRound, setCurrentRound] = useState(0)
  const [totalBlueNodes, setTotalBlueNodes] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const [llmStreamText, setLlmStreamText] = useState('')
  const abortRef = useRef<AbortController | null>(null)

  // Batch LLM token deltas via rAF to avoid per-token React renders
  const pendingDeltaRef = useRef('')
  const rafRef = useRef<number | null>(null)

  const flushDelta = useCallback(() => {
    rafRef.current = null
    const delta = pendingDeltaRef.current
    if (!delta) return
    pendingDeltaRef.current = ''
    setLlmStreamText((prev) => prev + delta)
  }, [])

  useEffect(() => {
    return () => {
      if (rafRef.current != null) cancelAnimationFrame(rafRef.current)
    }
  }, [])

  const handleEvent = useCallback((event: ResearchV2Event) => {
    const roundEvent: RoundEvent = {
      ...event,
      round: event.round ?? 0,
      timestamp: Date.now(),
    }

    switch (event.type) {
      case 'LOOP_STARTED':
        setRunStatus('running')
        break

      case 'ROUND_START': {
        const round = event.round ?? 0
        setCurrentRound(round)
        setRounds((prev) => {
          // Avoid duplicate round on reconnect
          if (prev.some((r) => r.round === round)) return prev
          return [...prev, { round, status: 'running', events: [roundEvent] }]
        })
        break
      }

      case 'GRAPH_READ':
        setRounds((prev) =>
          prev.map((r) =>
            r.round === event.round
              ? { ...r, blueNodeCount: event.blue_node_count, events: [...r.events, roundEvent] }
              : r,
          ),
        )
        break

      case 'LLM_CALL_START':
        setLlmStreamText('')
        pendingDeltaRef.current = ''
        setRounds((prev) =>
          prev.map((r) =>
            r.round === event.round
              ? { ...r, events: [...r.events, roundEvent] }
              : r,
          ),
        )
        break

      case 'LLM_TOKEN':
        if (event.delta) {
          pendingDeltaRef.current += event.delta
          if (rafRef.current == null) {
            rafRef.current = requestAnimationFrame(flushDelta)
          }
        }
        break

      case 'VALIDATION_FAILED':
        setLlmStreamText('')
        pendingDeltaRef.current = ''
        setRounds((prev) =>
          prev.map((r) =>
            r.round === event.round
              ? { ...r, events: [...r.events, roundEvent] }
              : r,
          ),
        )
        break

      case 'LLM_CALL_DONE':
        setRounds((prev) =>
          prev.map((r) =>
            r.round === event.round
              ? {
                  ...r,
                  newNodes: event.new_nodes,
                  newEdges: event.new_edges,
                  events: [...r.events, roundEvent],
                }
              : r,
          ),
        )
        break

      case 'GRAPH_WRITE_DONE':
        setRounds((prev) =>
          prev.map((r) =>
            r.round === event.round
              ? {
                  ...r,
                  nodesWritten: event.nodes_written,
                  edgesWritten: event.edges_written,
                  events: [...r.events, roundEvent],
                }
              : r,
          ),
        )
        break

      case 'ROUND_DONE':
        if (event.total_blue_nodes !== undefined) {
          setTotalBlueNodes(event.total_blue_nodes)
        }
        setLlmStreamText('')
        pendingDeltaRef.current = ''
        setRounds((prev) =>
          prev.map((r) =>
            r.round === event.round
              ? {
                  ...r,
                  status: 'done',
                  totalBlueNodes: event.total_blue_nodes,
                  events: [...r.events, roundEvent],
                }
              : r,
          ),
        )
        break

      case 'LOOP_STOPPED':
        setRunStatus('completed')
        setLlmStreamText('')
        break

      case 'LOOP_ERROR':
        setRunStatus('error')
        setError(event.error ?? 'Unknown error')
        setLlmStreamText('')
        setRounds((prev) =>
          prev.map((r) =>
            r.round === event.round
              ? { ...r, status: 'error', events: [...r.events, roundEvent] }
              : r,
          ),
        )
        break
    }
  }, [flushDelta])

  // Connect to an SSE stream (either start or subscribe)
  const connectStream = useCallback(
    (
      connector: (
        onEvent: (e: ResearchV2Event) => void,
        onDone: () => void,
        onError: (e: Error) => void,
        signal?: AbortSignal,
      ) => void,
    ) => {
      const abort = new AbortController()
      abortRef.current = abort

      connector(
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

  // On mount: check if loop is already running and auto-subscribe
  useEffect(() => {
    fetchResearchStatus()
      .then((status) => {
        if (status.is_running) {
          setRunStatus('running')
          setCurrentRound(status.current_round)
          // Create a placeholder round for the current running round
          if (status.current_round > 0) {
            setRounds([{
              round: status.current_round,
              status: 'running',
              events: [{
                type: 'ROUND_START',
                round: status.current_round,
                timestamp: Date.now(),
              }],
            }])
          }
          connectStream(subscribeResearch)
        }
      })
      .catch(() => {
        // Status endpoint failed — stay idle
      })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const startRun = useCallback(() => {
    setRounds([])
    setRunStatus('running')
    setCurrentRound(0)
    setTotalBlueNodes(0)
    setError(null)
    setLlmStreamText('')
    connectStream(startResearchV2)
  }, [connectStream])

  const stopRun = useCallback(async () => {
    try {
      await stopResearch()
    } catch {
      abortRef.current?.abort()
      abortRef.current = null
      setRunStatus((prev) => (prev === 'running' ? 'completed' : prev))
    }
  }, [])

  return { rounds, runStatus, currentRound, totalBlueNodes, error, llmStreamText, startRun, stopRun }
}
