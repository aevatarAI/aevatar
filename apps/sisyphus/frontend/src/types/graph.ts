export interface GraphNode {
  id: string
  label: string
  type: string
  properties?: Record<string, unknown>
  x?: number
  y?: number
}

export interface GraphEdge {
  id: string
  source: string
  target: string
  type: string
  properties?: Record<string, unknown>
}

export interface GraphSnapshot {
  nodes: GraphNode[]
  edges: GraphEdge[]
}

export interface TraverseResult {
  node: GraphNode
  neighbors: GraphNode[]
  edges: GraphEdge[]
}

export const NODE_COLORS: Record<string, string> = {
  Plan: '#3b82f6',
  Knowledge: '#22c55e',
  Active: '#f97316',
  Task: '#f97316',
  Default: '#8bb3d0',
}
