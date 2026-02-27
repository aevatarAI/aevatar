import type { GraphSnapshot, GraphNode, GraphEdge, TraverseResult } from '../types/graph'

interface RawNode {
  id: string
  type: string
  properties?: Record<string, unknown>
  [key: string]: unknown
}

interface RawEdge {
  id: string
  sourceNodeId?: string
  targetNodeId?: string
  source?: string
  target?: string
  type: string
  properties?: Record<string, unknown>
  [key: string]: unknown
}

function mapNode(raw: RawNode): GraphNode {
  const name = raw.properties?.name
  return {
    id: raw.id,
    label: typeof name === 'string' && name ? name : raw.id,
    type: raw.type,
    properties: raw.properties,
  }
}

function mapEdge(raw: RawEdge): GraphEdge {
  return {
    id: raw.id,
    source: raw.sourceNodeId ?? raw.source ?? '',
    target: raw.targetNodeId ?? raw.target ?? '',
    type: raw.type,
    properties: raw.properties,
  }
}

export async function fetchGraphSnapshot(): Promise<GraphSnapshot> {
  const res = await fetch('/api/graph/snapshot')
  if (!res.ok) throw new Error(`Failed to fetch graph snapshot: ${res.status}`)
  const data = await res.json()
  return {
    nodes: (data.nodes ?? []).map(mapNode),
    edges: (data.edges ?? []).map(mapEdge),
  }
}

export async function fetchNodeTraversal(nodeId: string, depth = 2): Promise<TraverseResult> {
  const res = await fetch(`/api/graph/nodes/${encodeURIComponent(nodeId)}/traverse?depth=${depth}`)
  if (!res.ok) throw new Error(`Failed to fetch node traversal: ${res.status}`)
  const data = await res.json()
  const node = data.node ? mapNode(data.node) : mapNode(data)
  return {
    node,
    neighbors: (data.neighbors ?? data.connectedNodes ?? []).map(mapNode),
    edges: (data.edges ?? data.relationships ?? []).map(mapEdge),
  }
}
