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
  Plan: '#00d4ff',
  Knowledge: '#00ff88',
  Active: '#ff6600',
  Task: '#ff00aa',
  Default: '#00ffff',
}
