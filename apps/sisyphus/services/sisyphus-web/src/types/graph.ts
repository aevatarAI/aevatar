export interface GraphNode {
  id: string
  label: string
  type: string
  properties?: Record<string, unknown>
  x?: number
  y?: number
  z?: number
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

// --- Dynamic color generation ---

function hashString(s: string): number {
  let h = 0
  for (let i = 0; i < s.length; i++) {
    h = ((h << 5) - h + s.charCodeAt(i)) | 0
  }
  return Math.abs(h)
}

function hslToHex(h: number, s: number, l: number): string {
  const a = s * Math.min(l, 1 - l)
  const f = (n: number) => {
    const k = (n + h / 30) % 12
    const color = l - a * Math.max(Math.min(k - 3, 9 - k, 1), -1)
    return Math.round(255 * color).toString(16).padStart(2, '0')
  }
  return `#${f(0)}${f(8)}${f(4)}`
}

function getLayerHueRange(type: string): [number, number] {
  if (type === 'raw') return [0, 15]
  if (type.startsWith('purified_')) return [200, 260]
  if (type.startsWith('verified_')) return [130, 170]
  return [270, 330]
}

function getEdgeHueRange(type: string): [number, number] {
  if (type.includes('proves')) return [20, 40]
  if (type.includes('references')) return [40, 60]
  if (type.includes('_from')) return [300, 330]
  return [180, 220]
}

const nodeColorCache = new Map<string, string>()
const edgeColorCache = new Map<string, string>()

export function getNodeColor(node: GraphNode): string {
  const type = node.type
  if (nodeColorCache.has(type)) return nodeColorCache.get(type)!
  const [hueMin, hueMax] = getLayerHueRange(type)
  const hash = hashString(type)
  const hue = hueMin + (hash % (hueMax - hueMin + 1))
  const sat = 0.7 + (hash % 20) / 100
  const lit = 0.55 + (hash % 15) / 100
  const color = hslToHex(hue, sat, lit)
  nodeColorCache.set(type, color)
  return color
}

export function getEdgeColor(edge: GraphEdge): string {
  const type = edge.type
  if (edgeColorCache.has(type)) return edgeColorCache.get(type)!
  const [hueMin, hueMax] = getEdgeHueRange(type)
  const hash = hashString(type)
  const hue = hueMin + (hash % (hueMax - hueMin + 1))
  const color = hslToHex(hue, 0.8, 0.55)
  edgeColorCache.set(type, color)
  return color
}

export function buildLegend(snapshot: GraphSnapshot): { nodeColors: Array<[string, string]>; edgeColors: Array<[string, string]> } {
  const nodeTypes = new Set<string>()
  const edgeTypes = new Set<string>()
  for (const n of snapshot.nodes) nodeTypes.add(n.type)
  for (const e of snapshot.edges) edgeTypes.add(e.type)

  const nodeColors = Array.from(nodeTypes).sort().map((t): [string, string] => {
    const color = nodeColorCache.get(t) ?? getNodeColor({ id: '', label: '', type: t })
    return [t, color]
  })
  const edgeColors = Array.from(edgeTypes).sort().map((t): [string, string] => {
    const color = edgeColorCache.get(t) ?? getEdgeColor({ id: '', source: '', target: '', type: t })
    return [t, color]
  })

  return { nodeColors, edgeColors }
}
