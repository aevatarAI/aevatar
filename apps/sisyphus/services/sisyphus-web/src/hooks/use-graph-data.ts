import { useState, useEffect, useCallback, useMemo } from 'react'
import { fetchGraphSnapshot, fetchNodeDetail, fetchNodeTraversal } from '../services/graph-service'
import { useAuth } from '../auth/useAuth'
import { useSettings } from '../settings/SettingsContext'
import type { GraphSnapshot, GraphNode, TraverseResult } from '../types/graph'

export interface GraphFilters {
  type: string
  layer: string
  edgeType: string
  search: string
}

export function useGraphData() {
  const { getAccessToken } = useAuth()
  const { settings } = useSettings()
  const graphId = settings.graphId
  const CACHE_KEY = `sisyphus_graph_cache_${graphId}`

  function getCachedSnapshot(): GraphSnapshot | null {
    try {
      const raw = sessionStorage.getItem(CACHE_KEY)
      if (!raw) return null
      return JSON.parse(raw)
    } catch { return null }
  }

  function cacheSnapshot(snapshot: GraphSnapshot) {
    try { sessionStorage.setItem(CACHE_KEY, JSON.stringify(snapshot)) }
    catch { /* sessionStorage full */ }
  }

  const [fullSnapshot, setFullSnapshot] = useState<GraphSnapshot | null>(() => getCachedSnapshot())
  const [selectedNode, setSelectedNode] = useState<GraphNode | null>(null)
  const [traverseResult, setTraverseResult] = useState<TraverseResult | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [filters, setFilters] = useState<GraphFilters>({ type: '', layer: 'purified', edgeType: '', search: '' })

  const filterOptions = useMemo(() => {
    if (!fullSnapshot) return { types: [] as string[], layers: [] as string[], edgeTypes: [] as string[] }
    const types = new Set<string>()
    const layers = new Set<string>()
    const edgeTypes = new Set<string>()
    for (const n of fullSnapshot.nodes) {
      if (n.type) types.add(n.type)
      if (n.type === 'raw') layers.add('raw')
      else if (n.type.startsWith('purified_')) layers.add('purified')
      else if (n.type.startsWith('verified_')) layers.add('verified')
    }
    for (const e of fullSnapshot.edges) {
      if (e.type) edgeTypes.add(e.type)
    }
    return {
      types: Array.from(types).sort(),
      layers: Array.from(layers).sort(),
      edgeTypes: Array.from(edgeTypes).sort(),
    }
  }, [fullSnapshot])

  const snapshot = useMemo<GraphSnapshot | null>(() => {
    if (!fullSnapshot) return null
    let nodes = fullSnapshot.nodes
    if (filters.layer) {
      if (filters.layer === 'raw') nodes = nodes.filter((n) => n.type === 'raw')
      else nodes = nodes.filter((n) => n.type.startsWith(filters.layer + '_'))
    }
    if (filters.type) {
      nodes = nodes.filter((n) => n.type === filters.type)
    }
    if (filters.search) {
      const q = filters.search.toLowerCase()
      nodes = nodes.filter((n) =>
        n.id.toLowerCase().includes(q) ||
        n.type?.toLowerCase().includes(q) ||
        (n.properties?.abstract as string)?.toLowerCase().includes(q) ||
        (n.properties?.name as string)?.toLowerCase().includes(q)
      )
    }
    const nodeIds = new Set(nodes.map((n) => n.id))
    let edges = fullSnapshot.edges.filter((e) => nodeIds.has(e.source) && nodeIds.has(e.target))
    if (filters.edgeType) {
      edges = edges.filter((e) => e.type === filters.edgeType)
    }
    return { nodes, edges }
  }, [fullSnapshot, filters])

  const totalFiltered = useMemo(() => snapshot?.nodes.length ?? 0, [snapshot])

  const refresh = useCallback(async () => {
    const token = getAccessToken()
    if (!token || !graphId) return
    setLoading(true)
    setError(null)
    try {
      const data = await fetchGraphSnapshot(graphId, token)
      setFullSnapshot(data)
      cacheSnapshot(data)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to fetch graph')
    } finally {
      setLoading(false)
    }
  }, [getAccessToken])

  useEffect(() => { refresh() }, [])

  const selectNode = useCallback(async (nodeId: string) => {
    const token = getAccessToken()
    // Show lightweight data immediately
    const lightNode = fullSnapshot?.nodes.find((n) => n.id === nodeId) ?? null
    setSelectedNode(lightNode)
    setTraverseResult(null)
    if (!token || !graphId) return

    // Fetch full node detail (body, all properties)
    try {
      const fullNode = await fetchNodeDetail(graphId, nodeId, token)
      setSelectedNode(fullNode)
    } catch (err) {
      console.error('[sisyphus] fetchNodeDetail failed:', err)
    }

    // Fetch connections (traverse)
    try {
      const result = await fetchNodeTraversal(graphId, nodeId, 2, token)
      setTraverseResult(result)
    } catch (err) {
      console.error('[sisyphus] fetchNodeTraversal failed:', err)
      setTraverseResult({ node: lightNode!, neighbors: [], edges: [] })
    }
  }, [fullSnapshot, getAccessToken, graphId])

  const clearSelection = useCallback(() => {
    setSelectedNode(null)
    setTraverseResult(null)
  }, [])

  return {
    snapshot, fullSnapshot, selectedNode, traverseResult,
    loading, error, refresh, selectNode, clearSelection,
    filters, setFilters, filterOptions, totalFiltered,
    totalNodes: fullSnapshot?.nodes.length ?? 0,
    totalEdges: fullSnapshot?.edges.length ?? 0,
  }
}
