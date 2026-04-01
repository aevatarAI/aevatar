import { describe, it, expect } from "vitest";
import { topologicalSort } from "../src/topological-sort.js";
import type { GraphNode, GraphEdge } from "../src/types.js";

function makeNode(id: string, type: string): GraphNode {
  return { id, type, properties: {} };
}

describe("topologicalSort", () => {
  it("sorts nodes with no edges in original order", () => {
    const nodes = [makeNode("a", "theorem"), makeNode("b", "lemma")];
    const result = topologicalSort(nodes, []);
    expect(result.map((n) => n.id)).toEqual(["a", "b"]);
  });

  it("places target before source (proves edge)", () => {
    const nodes = [makeNode("proof1", "proof"), makeNode("thm1", "theorem")];
    const edges: GraphEdge[] = [{ source: "proof1", target: "thm1", edge_type: "proves" }];
    const result = topologicalSort(nodes, edges);

    const thmIdx = result.findIndex((n) => n.id === "thm1");
    const proofIdx = result.findIndex((n) => n.id === "proof1");
    expect(thmIdx).toBeLessThan(proofIdx);
  });

  it("handles chain of dependencies", () => {
    const nodes = [
      makeNode("c", "corollary"),
      makeNode("b", "theorem"),
      makeNode("a", "definition"),
    ];
    const edges: GraphEdge[] = [
      { source: "b", target: "a", edge_type: "references" },
      { source: "c", target: "b", edge_type: "references" },
    ];
    const result = topologicalSort(nodes, edges);

    const aIdx = result.findIndex((n) => n.id === "a");
    const bIdx = result.findIndex((n) => n.id === "b");
    const cIdx = result.findIndex((n) => n.id === "c");
    expect(aIdx).toBeLessThan(bIdx);
    expect(bIdx).toBeLessThan(cIdx);
  });

  it("falls back to type-based ordering on cycles", () => {
    const nodes = [
      makeNode("a", "theorem"),
      makeNode("b", "definition"),
    ];
    const edges: GraphEdge[] = [
      { source: "a", target: "b", edge_type: "references" },
      { source: "b", target: "a", edge_type: "references" },
    ];
    const result = topologicalSort(nodes, edges);

    // definition (order 2) should come before theorem (order 5)
    const defIdx = result.findIndex((n) => n.id === "b");
    const thmIdx = result.findIndex((n) => n.id === "a");
    expect(defIdx).toBeLessThan(thmIdx);
  });

  it("ignores edges referencing non-existent nodes", () => {
    const nodes = [makeNode("a", "theorem")];
    const edges: GraphEdge[] = [
      { source: "a", target: "nonexistent", edge_type: "references" },
    ];
    const result = topologicalSort(nodes, edges);
    expect(result).toHaveLength(1);
    expect(result[0].id).toBe("a");
  });

  it("handles empty nodes array", () => {
    const result = topologicalSort([], []);
    expect(result).toEqual([]);
  });

  it("handles complex graph with multiple roots", () => {
    const nodes = [
      makeNode("d1", "definition"),
      makeNode("d2", "definition"),
      makeNode("t1", "theorem"),
      makeNode("p1", "proof"),
    ];
    const edges: GraphEdge[] = [
      { source: "t1", target: "d1", edge_type: "references" },
      { source: "t1", target: "d2", edge_type: "references" },
      { source: "p1", target: "t1", edge_type: "proves" },
    ];
    const result = topologicalSort(nodes, edges);

    const d1Idx = result.findIndex((n) => n.id === "d1");
    const d2Idx = result.findIndex((n) => n.id === "d2");
    const t1Idx = result.findIndex((n) => n.id === "t1");
    const p1Idx = result.findIndex((n) => n.id === "p1");

    expect(d1Idx).toBeLessThan(t1Idx);
    expect(d2Idx).toBeLessThan(t1Idx);
    expect(t1Idx).toBeLessThan(p1Idx);
  });
});
