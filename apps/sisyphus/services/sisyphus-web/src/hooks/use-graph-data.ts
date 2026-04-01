import { useState, useEffect, useCallback, useMemo } from 'react'
import { fetchGraphSnapshot, fetchNodeTraversal } from '../services/graph-service'
import { useAuth } from '../auth/useAuth'
import { useSettings } from '../settings/SettingsContext'
import type { GraphSnapshot, GraphNode, TraverseResult } from '../types/graph'

const INITIAL_NODE_LIMIT = 200

export interface GraphFilters {
  type: string
  status: string
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
    } catch {
      return null
    }
  }

  function cacheSnapshot(snapshot: GraphSnapshot) {
    try {
      sessionStorage.setItem(CACHE_KEY, JSON.stringify(snapshot))
    } catch { /* sessionStorage full */ }
  }

  const [fullSnapshot, setFullSnapshot] = useState<GraphSnapshot | null>(() => getCachedSnapshot())
  const [selectedNode, setSelectedNode] = useState<GraphNode | null>(null)
  const [traverseResult, setTraverseResult] = useState<TraverseResult | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [filters, setFilters] = useState<GraphFilters>({ type: '', status: '', search: '' })
  const [nodeLimit, setNodeLimit] = useState(INITIAL_NODE_LIMIT)

  // Available filter options derived from full snapshot
  const filterOptions = useMemo(() => {
    if (!fullSnapshot) return { types: [], statuses: [] }
    const types = new Set<string>()
    const statuses = new Set<string>()
    for (const n of fullSnapshot.nodes) {
      if (n.type) types.add(n.type)
      const s = n.properties?.sisyphus_status
      if (typeof s === 'string') statuses.add(s)
    }
    return {
      types: Array.from(types).sort(),
      statuses: Array.from(statuses).sort(),
    }
  }, [fullSnapshot])

  // Filtered + limited snapshot for rendering
  const snapshot = useMemo<GraphSnapshot | null>(() => {
    if (!fullSnapshot) return null

    let nodes = fullSnapshot.nodes

    // Apply filters
    if (filters.type) {
      nodes = nodes.filter((n) => n.type === filters.type)
    }
    if (filters.status) {
      nodes = nodes.filter((n) => n.properties?.sisyphus_status === filters.status)
    }
    if (filters.search) {
      const q = filters.search.toLowerCase()
      nodes = nodes.filter((n) =>
        n.id.toLowerCase().includes(q) ||
        n.type?.toLowerCase().includes(q) ||
        (n.properties?.abstract as string)?.toLowerCase().includes(q) ||
        (n.properties?.name as string)?.toLowerCase().includes(q) ||
        (n.properties?.body as string)?.toLowerCase().includes(q)
      )
    }

    // Limit
    const limited = nodes.slice(0, nodeLimit)
    const nodeIds = new Set(limited.map((n) => n.id))

    // Only include edges where both source and target are in the visible set
    const edges = fullSnapshot.edges.filter(
      (e) => nodeIds.has(e.source) && nodeIds.has(e.target)
    )

    return { nodes: limited, edges }
  }, [fullSnapshot, filters, nodeLimit])

  const totalFiltered = useMemo(() => {
    if (!fullSnapshot) return 0
    let nodes = fullSnapshot.nodes
    if (filters.type) nodes = nodes.filter((n) => n.type === filters.type)
    if (filters.status) nodes = nodes.filter((n) => n.properties?.sisyphus_status === filters.status)
    if (filters.search) {
      const q = filters.search.toLowerCase()
      nodes = nodes.filter((n) =>
        n.id.toLowerCase().includes(q) ||
        n.type?.toLowerCase().includes(q) ||
        (n.properties?.abstract as string)?.toLowerCase().includes(q) ||
        (n.properties?.name as string)?.toLowerCase().includes(q)
      )
    }
    return nodes.length
  }, [fullSnapshot, filters])

  const refresh = useCallback(async () => {
    const token = getAccessToken()
    if (!token || !graphId) return

    setLoading(true)
    setError(null)
    try {
      const data = await fetchGraphSnapshot(graphId, token)
      setFullSnapshot(data)
      cacheSnapshot(data)
      setNodeLimit(INITIAL_NODE_LIMIT)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to fetch graph')
    } finally {
      setLoading(false)
    }
  }, [getAccessToken])

  // Load from API if no cache, otherwise use cache and refresh in background
  useEffect(() => {
    if (fullSnapshot) {
      // Have cache — refresh in background
      refresh()
    } else {
      refresh()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const loadMore = useCallback(() => {
    setNodeLimit((prev) => prev + 200)
  }, [])

  const selectNode = useCallback(async (nodeId: string) => {
    const token = getAccessToken()
    const node = fullSnapshot?.nodes.find((n) => n.id === nodeId) ?? null
    setSelectedNode(node)
    setTraverseResult(null)
    if (!token || !graphId) return
    try {
      const result = await fetchNodeTraversal(graphId, nodeId, 2, token)
      setTraverseResult(result)
    } catch {
      // Traverse is supplementary
    }
  }, [fullSnapshot, getAccessToken])

  const clearSelection = useCallback(() => {
    setSelectedNode(null)
    setTraverseResult(null)
  }, [])

  return {
    snapshot,
    fullSnapshot,
    selectedNode,
    traverseResult,
    loading,
    error,
    refresh,
    selectNode,
    clearSelection,
    filters,
    setFilters,
    filterOptions,
    nodeLimit,
    loadMore,
    totalFiltered,
    totalNodes: fullSnapshot?.nodes.length ?? 0,
    totalEdges: fullSnapshot?.edges.length ?? 0,
  }
}
