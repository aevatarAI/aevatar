import type { WorkflowEvent } from './workflow-service'
import type { ResearchV2Event } from '../api'

/**
 * Maps aevatar mainnet workflow SSE events to the Sisyphus UI event format
 * that the existing useResearchStream hook expects.
 */

/** Parse round number from step names like "research_loop_iter_3" */
function parseRoundFromStep(stepName: string): number | undefined {
  const match = stepName.match(/research_loop_iter_(\d+)/)
  return match ? parseInt(match[1], 10) : undefined
}

export function adaptWorkflowEvent(event: WorkflowEvent): ResearchV2Event | null {
  const stepName = event.step_name ?? ''
  const data = event.data ?? {}

  switch (event.type) {
    case 'WorkflowStarted':
      return { type: 'LOOP_STARTED' }

    case 'StepStarted': {
      const round = parseRoundFromStep(stepName)
      if (round !== undefined) {
        return { type: 'ROUND_START', round }
      }
      if (stepName === 'generate') {
        return { type: 'LLM_CALL_START', round: data.round as number | undefined }
      }
      return null
    }

    case 'StepCompleted': {
      if (stepName === 'generate') {
        return {
          type: 'LLM_CALL_DONE',
          round: data.round as number | undefined,
          new_nodes: data.new_nodes as number | undefined,
          new_edges: data.new_edges as number | undefined,
        }
      }
      if (stepName === 'validate_format') {
        // If the step completed but didn't cause an exit, it means validation failed
        // and the loop retried
        if (!data.exit) {
          return {
            type: 'VALIDATION_FAILED',
            round: data.round as number | undefined,
            attempt: data.attempt as number | undefined,
            errors: data.errors as string[] | undefined,
          }
        }
        return null
      }
      if (stepName === 'write_black_nodes') {
        return {
          type: 'GRAPH_WRITE_DONE',
          round: data.round as number | undefined,
          nodes_written: data.nodes_written as number | undefined,
          edges_written: data.edges_written as number | undefined,
        }
      }
      // Round completion from loop iteration step
      const round = parseRoundFromStep(stepName)
      if (round !== undefined) {
        return {
          type: 'ROUND_DONE',
          round,
          total_blue_nodes: data.total_blue_nodes as number | undefined,
        }
      }
      return null
    }

    case 'WorkflowCompleted':
      return { type: 'LOOP_STOPPED' }

    case 'WorkflowFailed':
      return {
        type: 'LOOP_ERROR',
        error: event.error ?? data.error as string | undefined ?? 'Workflow failed',
      }

    case 'StepOutput': {
      // Stream LLM token deltas
      if (stepName === 'generate' && data.delta) {
        return {
          type: 'LLM_TOKEN',
          delta: data.delta as string,
          round: data.round as number | undefined,
        }
      }
      // Graph read event
      if (stepName === 'read_graph') {
        return {
          type: 'GRAPH_READ',
          round: data.round as number | undefined,
          blue_node_count: data.blue_node_count as number | undefined,
        }
      }
      return null
    }

    default:
      return null
  }
}
