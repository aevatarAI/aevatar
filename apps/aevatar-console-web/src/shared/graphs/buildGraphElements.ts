import type { Edge, Node } from "@xyflow/react";
import type {
  WorkflowActorGraphEdge,
  WorkflowActorGraphNode,
} from "@/shared/models/runtime/actors";
import type {
  WorkflowAuthoringDefinition,
  WorkflowAuthoringEdge,
} from "@/shared/models/runtime/authoring";
import type {
  WorkflowCatalogDefinition,
  WorkflowCatalogEdge,
  WorkflowCatalogItemDetail,
} from "@/shared/models/runtime/catalog";

type WorkflowGraphDetail = {
  definition: WorkflowCatalogDefinition | WorkflowAuthoringDefinition;
  edges: Array<WorkflowCatalogEdge | WorkflowAuthoringEdge>;
};

type WorkflowGraphEdgeLike = {
  from: string;
  to: string;
  label: string;
};

type LayoutNode = {
  id: string;
  label: string;
  subtitle?: string;
};

function buildLevels(
  rootId: string,
  edges: Array<{ from: string; to: string }>
): Map<string, number> {
  const outgoing = new Map<string, string[]>();
  for (const edge of edges) {
    const siblings = outgoing.get(edge.from) ?? [];
    siblings.push(edge.to);
    outgoing.set(edge.from, siblings);
  }

  const levels = new Map<string, number>([[rootId, 0]]);
  const queue = [rootId];

  while (queue.length > 0) {
    const current = queue.shift()!;
    const nextLevel = (levels.get(current) ?? 0) + 1;
    for (const next of outgoing.get(current) ?? []) {
      if (levels.has(next)) {
        continue;
      }

      levels.set(next, nextLevel);
      queue.push(next);
    }
  }

  return levels;
}

function layoutNodes(
  nodes: LayoutNode[],
  edges: Array<{ from: string; to: string }>,
  rootId: string
): Node[] {
  const levels = buildLevels(rootId, edges);
  const groups = new Map<number, LayoutNode[]>();

  for (const node of nodes) {
    const level = levels.get(node.id) ?? 0;
    const entries = groups.get(level) ?? [];
    entries.push(node);
    groups.set(level, entries);
  }

  return nodes.map((node) => {
    const level = levels.get(node.id) ?? 0;
    const siblings = groups.get(level) ?? [node];
    const index = siblings.findIndex((entry) => entry.id === node.id);

    return {
      id: node.id,
      position: {
        x: level * 260,
        y: index * 130,
      },
      data: {
        label: node.label,
        subtitle: node.subtitle,
      },
      type: "default",
    };
  });
}

export function buildActorGraphElements(
  nodes: WorkflowActorGraphNode[],
  edges: WorkflowActorGraphEdge[],
  rootId: string
): { nodes: Node[]; edges: Edge[] } {
  const mappedNodes = nodes.map((node) => ({
    id: node.nodeId,
    label:
      node.properties.stepId || node.properties.workflowName || node.nodeId,
    subtitle: node.nodeType,
  }));

  return {
    nodes: layoutNodes(
      mappedNodes,
      edges.map((edge) => ({
        from: edge.fromNodeId,
        to: edge.toNodeId,
      })),
      rootId
    ),
    edges: edges.map((edge) => ({
      id: edge.edgeId,
      source: edge.fromNodeId,
      target: edge.toNodeId,
      label: edge.edgeType,
      animated: edge.edgeType === "OWNS",
    })),
  };
}

function buildWorkflowEdges(detail: WorkflowGraphDetail) {
  const edges: WorkflowGraphEdgeLike[] = [...detail.edges];

  if (edges.length > 0) {
    return edges;
  }

  return detail.definition.steps.flatMap((step) => {
    const derived: WorkflowGraphEdgeLike[] = [];
    if (step.next) {
      derived.push({ from: step.id, to: step.next, label: "next" });
    }

    for (const [branch, target] of Object.entries(step.branches ?? {})) {
      if (target) {
        derived.push({ from: step.id, to: target, label: branch });
      }
    }

    for (const child of step.children ?? []) {
      derived.push({
        from: step.id,
        to: child.id,
        label: child.type || "child",
      });
    }

    return derived;
  });
}

export function buildWorkflowGraphElements(
  detail: WorkflowCatalogItemDetail | WorkflowGraphDetail
): { nodes: Node[]; edges: Edge[] } {
  const roleNodes: LayoutNode[] = detail.definition.roles.map((role) => ({
    id: `role:${role.id}`,
    label: role.name || role.id,
    subtitle: "Role",
  }));

  const stepNodes: LayoutNode[] = detail.definition.steps.map((step) => ({
    id: step.id,
    label: step.id,
    subtitle: step.type,
  }));

  const workflowEdges = buildWorkflowEdges(detail);
  const roleEdges = detail.definition.steps
    .filter((step) => step.targetRole)
    .map((step) => ({
      from: `role:${step.targetRole}`,
      to: step.id,
      label: "targets",
    }));

  const allEdges = [...roleEdges, ...workflowEdges];

  return {
    nodes: layoutNodes(
      [...roleNodes, ...stepNodes],
      allEdges,
      roleNodes[0]?.id ?? stepNodes[0]?.id ?? "root"
    ),
    edges: allEdges.map((edge, index) => ({
      id: `${edge.from}-${edge.to}-${index}`,
      source: edge.from,
      target: edge.to,
      label: edge.label,
      animated: edge.label === "targets",
    })),
  };
}
