import type { GraphNode, GraphEdge } from "./types.js";

const TYPE_ORDER: Record<string, number> = {
  axiom: 0, notation: 1, definition: 2,
  lemma: 3, proposition: 4, theorem: 5,
  corollary: 6, conjecture: 7, proof: 8,
  example: 9, remark: 10, observation: 11,
  note: 12, conclusion: 13,
};

/**
 * Topological sort via Kahn's algorithm.
 * Falls back to type-based ordering on cycles.
 *
 * Edge direction: source depends on target (source references/proves target),
 * so target should come before source in the output.
 */
export function topologicalSort(nodes: GraphNode[], edges: GraphEdge[]): GraphNode[] {
  const nodeIds = new Set(nodes.map((n) => n.id));
  const adjacency = new Map<string, string[]>();
  const inDegree = new Map<string, number>();

  for (const node of nodes) {
    adjacency.set(node.id, []);
    inDegree.set(node.id, 0);
  }

  for (const edge of edges) {
    if (!nodeIds.has(edge.source) || !nodeIds.has(edge.target)) continue;

    // target should come before source
    adjacency.get(edge.target)!.push(edge.source);
    inDegree.set(edge.source, (inDegree.get(edge.source) ?? 0) + 1);
  }

  const queue: string[] = [];
  for (const [id, deg] of inDegree) {
    if (deg === 0) queue.push(id);
  }

  const sorted: string[] = [];
  while (queue.length > 0) {
    const current = queue.shift()!;
    sorted.push(current);

    for (const neighbor of adjacency.get(current) ?? []) {
      const newDeg = (inDegree.get(neighbor) ?? 1) - 1;
      inDegree.set(neighbor, newDeg);
      if (newDeg === 0) queue.push(neighbor);
    }
  }

  const nodeMap = new Map(nodes.map((n) => [n.id, n]));

  if (sorted.length === nodes.length) {
    return sorted.map((id) => nodeMap.get(id)!);
  }

  // Cycle detected — fall back to type-based ordering
  return [...nodes].sort((a, b) => {
    const aOrder = TYPE_ORDER[a.type] ?? 99;
    const bOrder = TYPE_ORDER[b.type] ?? 99;
    return aOrder - bOrder;
  });
}
