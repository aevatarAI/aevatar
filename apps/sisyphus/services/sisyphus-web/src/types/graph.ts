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

/** Sisyphus status-based node coloring */
export const STATUS_COLORS: Record<string, string> = {
  raw: '#ff4444',       // Red
  purified: '#4488ff',  // Blue
  verified: '#e0e0e0',  // Near-white (black nodes on dark bg)
}

/** Get node color based on sisyphus_status, falling back to type-based coloring */
export function getNodeColor(node: GraphNode): string {
  const status = node.properties?.sisyphus_status
  if (typeof status === 'string' && STATUS_COLORS[status]) {
    return STATUS_COLORS[status]
  }
  return NODE_COLORS[node.type] ?? NODE_COLORS.Default
}
