import { useState, useCallback, useRef } from 'react'
import { startResearchV2, stopResearch } from '../api'
import type { ResearchV2Event } from '../api'
import type { RunStatus, RoundEvent, RoundState } from '../types'

export function useResearchStream() {
  const [rounds, setRounds] = useState<RoundState[]>([])
  const [runStatus, setRunStatus] = useState<RunStatus>('idle')
  const [currentRound, setCurrentRound] = useState(0)
  const [totalBlueNodes, setTotalBlueNodes] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const abortRef = useRef<AbortController | null>(null)

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
        setRounds((prev) => [
          ...prev,
          { round, status: 'running', events: [roundEvent] },
        ])
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
      case 'VALIDATION_FAILED':
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
        break

      case 'LOOP_ERROR':
        setRunStatus('error')
        setError(event.error ?? 'Unknown error')
        setRounds((prev) =>
          prev.map((r) =>
            r.round === event.round
              ? { ...r, status: 'error', events: [...r.events, roundEvent] }
              : r,
          ),
        )
        break
    }
  }, [])

  const startRun = useCallback(() => {
    setRounds([])
    setRunStatus('running')
    setCurrentRound(0)
    setTotalBlueNodes(0)
    setError(null)

    const abort = new AbortController()
    abortRef.current = abort

    startResearchV2(
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
  }, [handleEvent])

  const stopRun = useCallback(async () => {
    try {
      await stopResearch()
    } catch {
      // If stop fails, abort the SSE connection
      abortRef.current?.abort()
      abortRef.current = null
      setRunStatus((prev) => (prev === 'running' ? 'completed' : prev))
    }
  }, [])

  return { rounds, runStatus, currentRound, totalBlueNodes, error, startRun, stopRun }
}
