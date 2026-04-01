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
  // chrono-graph returns user properties as flat top-level fields (JsonExtensionData)
  const SYSTEM_KEYS = new Set(['id', 'graphId', 'type', 'createdBy', 'createdAt', 'updatedBy', 'updatedAt', 'properties'])
  const properties: Record<string, unknown> = raw.properties ? { ...raw.properties } : {}
  for (const [k, v] of Object.entries(raw)) {
    if (!SYSTEM_KEYS.has(k) && v !== undefined && v !== null) {
      properties[k] = v
    }
  }

  const name = properties.name
  const abstract = properties.abstract
  let label: string
  if (typeof name === 'string' && name) {
    label = name
  } else if (typeof abstract === 'string' && abstract) {
    label = abstract.length > 60 ? abstract.slice(0, 57) + '...' : abstract
  } else {
    label = `${raw.type ?? 'node'} · ${raw.id.slice(0, 8)}`
  }
  return { id: raw.id, label, type: raw.type, properties }
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

export async function fetchNodeDetail(graphId: string, nodeId: string, accessToken: string): Promise<GraphNode> {
  const url = proxyUrl(`/api/graphs/${encodeURIComponent(graphId)}/nodes/${encodeURIComponent(nodeId)}`)
  const res = await fetch(url, { headers: authHeaders(accessToken) })
  if (!res.ok) throw new Error(`Failed to fetch node: ${res.status}`)
  const data = await res.json()
  return mapNode(data)
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
  // chrono-graph traverse returns parents/children/parentEdges/childEdges
  const neighbors = [
    ...(data.parents ?? []),
    ...(data.children ?? []),
    ...(data.neighbors ?? []),
    ...(data.connectedNodes ?? []),
    ...(data.upstream ?? []),
    ...(data.downstream ?? []),
  ].map(mapNode)
  // Deduplicate by id
  const seen = new Set<string>()
  const uniqueNeighbors = neighbors.filter((n) => {
    if (seen.has(n.id)) return false
    seen.add(n.id)
    return true
  })
  const edges = [
    ...(data.parentEdges ?? []),
    ...(data.childEdges ?? []),
    ...(data.edges ?? []),
    ...(data.relationships ?? []),
  ].map(mapEdge)
  return { node, neighbors: uniqueNeighbors, edges }
}
