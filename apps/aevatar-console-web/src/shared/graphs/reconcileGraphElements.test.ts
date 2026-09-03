import type { Edge, Node } from "@xyflow/react";

import {
  reconcileGraphEdges,
  reconcileGraphNodes,
} from "./reconcileGraphElements";

const node = (id: string, overrides: Partial<Node> = {}): Node => ({
  id,
  position: { x: 0, y: 0 },
  data: {},
  ...overrides,
});

const edge = (id: string, source: string, target: string, overrides: Partial<Edge> = {}): Edge => ({
  id,
  source,
  target,
  ...overrides,
});

describe("reconcileGraphElements", () => {
  it("returns the previous node array and objects when incoming nodes are unchanged", () => {
    const previous = [
      node("step:alpha", { data: { label: "Alpha" }, position: { x: 24, y: 48 } }),
      node("step:beta", { data: { label: "Beta" }, selected: true }),
    ];
    const incoming = previous.map((element) => ({
      ...element,
      data: { ...element.data },
      position: { ...element.position },
    }));

    const result = reconcileGraphNodes(previous, incoming, "step:beta");

    expect(result).toBe(previous);
    expect(result[0]).toBe(previous[0]);
    expect(result[1]).toBe(previous[1]);
  });

  it("replaces only the changed node in a 500-node graph", () => {
    const previous = Array.from({ length: 500 }, (_, index) =>
      node(`step:${index}`, { data: { label: `Step ${index}` }, position: { x: index, y: index * 2 } }),
    );
    const incoming = previous.map((element) => ({
      ...element,
      data: { ...element.data },
      position: { ...element.position },
    }));
    incoming[237] = {
      ...incoming[237],
      data: { label: "Changed step" },
    };

    const result = reconcileGraphNodes(previous, incoming);

    expect(result).not.toBe(previous);
    expect(result[237]).not.toBe(previous[237]);
    expect(result[0]).toBe(previous[0]);
    expect(result[236]).toBe(previous[236]);
    expect(result[238]).toBe(previous[238]);
    expect(result[499]).toBe(previous[499]);
  });

  it("replaces only the previous and next selected nodes during a selection transition", () => {
    const previous = [
      node("step:09", { selected: false }),
      node("step:10", { selected: true }),
      node("step:11", { selected: false }),
      node("step:12", { selected: false }),
    ];
    const incoming = previous.map((element) => ({ ...element, selected: false }));

    const result = reconcileGraphNodes(previous, incoming, "step:11");

    expect(result[0]).toBe(previous[0]);
    expect(result[1]).not.toBe(previous[1]);
    expect(result[1].selected).toBe(false);
    expect(result[2]).not.toBe(previous[2]);
    expect(result[2].selected).toBe(true);
    expect(result[3]).toBe(previous[3]);
  });

  it("replaces a status-only node update while preserving every edge reference", () => {
    const previousNodes = [
      node("step:source", { data: { executionStatus: "running", label: "Source" } }),
      node("step:target", { data: { executionStatus: "pending", label: "Target" } }),
    ];
    const incomingNodes = [
      node("step:source", { data: { executionStatus: "completed", label: "Source" } }),
      node("step:target", { data: { executionStatus: "pending", label: "Target" } }),
    ];
    const previousEdges = [edge("edge:source-target", "step:source", "step:target")];
    const incomingEdges = previousEdges.map((element) => ({ ...element }));

    const nodes = reconcileGraphNodes(previousNodes, incomingNodes);
    const edges = reconcileGraphEdges(previousEdges, incomingEdges);

    expect(nodes[0]).not.toBe(previousNodes[0]);
    expect(nodes[1]).toBe(previousNodes[1]);
    expect(edges).toBe(previousEdges);
    expect(edges[0]).toBe(previousEdges[0]);
  });

  it("returns the previous edge array and objects when incoming edges are unchanged", () => {
    const previous = [
      edge("edge:alpha-beta", "step:alpha", "step:beta", {
        data: { condition: "success" },
        label: "continues",
        style: { stroke: "#1677ff" },
      }),
      edge("edge:beta-gamma", "step:beta", "step:gamma", { animated: true }),
    ];
    const incoming = previous.map((element) => ({
      ...element,
      data: element.data ? { ...element.data } : element.data,
      style: element.style ? { ...element.style } : element.style,
    }));

    const result = reconcileGraphEdges(previous, incoming);

    expect(result).toBe(previous);
    expect(result[0]).toBe(previous[0]);
    expect(result[1]).toBe(previous[1]);
  });

  it("keeps reused objects in the incoming node and edge order", () => {
    const previousNodes = [node("step:first"), node("step:second")];
    const previousEdges = [
      edge("edge:first", "step:first", "step:second"),
      edge("edge:second", "step:second", "step:first"),
    ];

    const nodes = reconcileGraphNodes(previousNodes, [previousNodes[1], previousNodes[0]]);
    const edges = reconcileGraphEdges(previousEdges, [previousEdges[1], previousEdges[0]]);

    expect(nodes).not.toBe(previousNodes);
    expect(nodes[0]).toBe(previousNodes[1]);
    expect(nodes[1]).toBe(previousNodes[0]);
    expect(edges).not.toBe(previousEdges);
    expect(edges[0]).toBe(previousEdges[1]);
    expect(edges[1]).toBe(previousEdges[0]);
  });
});
