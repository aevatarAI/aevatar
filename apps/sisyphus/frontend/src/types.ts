export type RunStatus = 'idle' | 'running' | 'completed' | 'error'

export interface RoundEvent {
  type: string
  round: number
  timestamp: number
  blue_node_count?: number
  new_nodes?: number
  new_edges?: number
  nodes_written?: number
  edges_written?: number
  total_blue_nodes?: number
  attempt?: number
  errors?: string[]
  reason?: string
  error?: string
}

export interface RoundState {
  round: number
  status: 'running' | 'done' | 'error'
  events: RoundEvent[]
  blueNodeCount?: number
  newNodes?: number
  newEdges?: number
  nodesWritten?: number
  edgesWritten?: number
  totalBlueNodes?: number
}
