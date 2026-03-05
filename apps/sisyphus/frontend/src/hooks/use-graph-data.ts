import { useState, useEffect, useCallback } from 'react'
import { fetchGraphSnapshot, fetchNodeTraversal } from '../api/graph-api'
import type { GraphSnapshot, GraphNode, TraverseResult } from '../types/graph'

export function useGraphData() {
  const [snapshot, setSnapshot] = useState<GraphSnapshot | null>(null)
  const [selectedNode, setSelectedNode] = useState<GraphNode | null>(null)
  const [traverseResult, setTraverseResult] = useState<TraverseResult | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const data = await fetchGraphSnapshot()
      setSnapshot(data)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to fetch graph')
    } finally {
      setLoading(false)
    }
  }, [])

  // Load once on mount
  useEffect(() => {
    refresh()
  }, [refresh])

  const selectNode = useCallback(async (nodeId: string) => {
    const node = snapshot?.nodes.find((n) => n.id === nodeId) ?? null
    setSelectedNode(node)
    setTraverseResult(null)
    try {
      const result = await fetchNodeTraversal(nodeId)
      setTraverseResult(result)
    } catch {
      // Traverse is supplementary; don't block on failure
    }
  }, [snapshot])

  const clearSelection = useCallback(() => {
    setSelectedNode(null)
    setTraverseResult(null)
  }, [])

  return { snapshot, selectedNode, traverseResult, loading, error, refresh, selectNode, clearSelection }
}
