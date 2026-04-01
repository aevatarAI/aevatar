import type { GraphSnapshot, GraphNode, GraphEdge, TraverseResult } from '../types/graph'

const PROXY_URL = import.meta.env.VITE_NYXID_PROXY_URL
const GRAPH_SLUG = 'chrono-graph-service'

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

function proxyUrl(path: string): string {
  return `${PROXY_URL}/api/v1/proxy/s/${GRAPH_SLUG}${path}`
}

function authHeaders(accessToken: string): HeadersInit {
  return { Authorization: `Bearer ${accessToken}` }
}

export async function fetchGraphSnapshot(graphId: string, accessToken: string): Promise<GraphSnapshot> {
  const url = proxyUrl(`/api/graphs/${encodeURIComponent(graphId)}/snapshot-light`)
  const res = await fetch(url, { headers: authHeaders(accessToken) })
  if (!res.ok) throw new Error(`Failed to fetch graph snapshot: ${res.status}`)
  const data = await res.json()
  return {
    nodes: (data.nodes ?? []).map(mapNode),
    edges: (data.edges ?? []).map(mapEdge),
  }
}

export async function fetchNodeTraversal(
  graphId: string,
  nodeId: string,
  depth: number,
  accessToken: string,
): Promise<TraverseResult> {
  const url = proxyUrl(
    `/api/graphs/${encodeURIComponent(graphId)}/nodes/${encodeURIComponent(nodeId)}/traverse?depth=${depth}`,
  )
  const res = await fetch(url, { headers: authHeaders(accessToken) })
  if (!res.ok) throw new Error(`Failed to fetch node traversal: ${res.status}`)
  const data = await res.json()
  const node = data.node ? mapNode(data.node) : mapNode(data)
  return {
    node,
    neighbors: (data.neighbors ?? data.connectedNodes ?? []).map(mapNode),
    edges: (data.edges ?? data.relationships ?? []).map(mapEdge),
  }
}
