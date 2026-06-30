import {
  Background,
  BackgroundVariant,
  Controls,
  MarkerType,
  Position,
  ReactFlow,
  type Edge,
  type NodeMouseHandler,
  type Node,
  type OnMove,
  type ReactFlowInstance,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import React from "react";
import type {
  MissionWallWorkflowGraph,
  MissionWallWorkflowStepEdge,
  MissionWallWorkflowStepNode,
} from "../models";
import { WorkflowStepNode } from "./WorkflowStepNode";

const NODE_WIDTH = 260;
const NODE_HEIGHT = 112;
const NODE_X_GAP = 340;
const NODE_Y_TOP = 118;
const NODE_Y_BOTTOM = 258;
const FOCUS_WINDOW_SIZE = 5;

type WorkflowReplayNodeData = {
  readonly node: MissionWallWorkflowStepNode;
};

type WorkflowReplayNode = Node<WorkflowReplayNodeData>;
type WorkflowReplayEdge = Edge<{ readonly edge: MissionWallWorkflowStepEdge }>;

const nodeTypes = {
  missionWallWorkflowStep: WorkflowStepNode,
};

function focusWindowStepIds(
  graph: MissionWallWorkflowGraph | undefined,
): string[] {
  const nodes = graph?.nodes ?? [];
  if (!nodes.length) {
    return [];
  }

  const selectedIndex = Math.max(
    0,
    nodes.findIndex((node) => node.stepId === graph?.selectedStepId),
  );
  const maxStart = Math.max(0, nodes.length - FOCUS_WINDOW_SIZE);
  const start =
    nodes.length <= FOCUS_WINDOW_SIZE
      ? 0
      : Math.min(Math.max(0, selectedIndex - 2), maxStart);

  return nodes.slice(start, start + FOCUS_WINDOW_SIZE).map((node) => node.id);
}

function graphIdentityKey(graph: MissionWallWorkflowGraph | undefined): string {
  const nodeIds = (graph?.nodes ?? []).map((node) => node.id).join("|");
  const runId = graph?.nodes.find((node) => node.runId)?.runId ?? "";
  return `${runId}:${nodeIds}`;
}

function graphViewportKey(graph: MissionWallWorkflowGraph | undefined): string {
  return `${graphIdentityKey(graph)}:${graph?.selectedStepId ?? ""}`;
}

function toFlowNodes(graph: MissionWallWorkflowGraph | undefined): WorkflowReplayNode[] {
  return (graph?.nodes ?? []).map((node, index) => ({
    data: { node },
    id: node.id,
    position: {
      x: index * NODE_X_GAP,
      y:
        node.status === "failed" || node.status === "waiting"
          ? NODE_Y_BOTTOM
          : index % 2 === 0
            ? NODE_Y_TOP
            : NODE_Y_BOTTOM,
    },
    sourcePosition: Position.Right,
    targetPosition: Position.Left,
    type: "missionWallWorkflowStep",
  }));
}

function edgeTone(
  edge: MissionWallWorkflowStepEdge,
  targetNode?: MissionWallWorkflowStepNode,
): {
  readonly animated: boolean;
  readonly color: string;
  readonly dash?: string;
  readonly width: number;
} {
  if (targetNode?.status === "failed") {
    return {
      animated: false,
      color: "#f87171",
      width: 4,
    };
  }

  if (edge.focused) {
    return {
      animated: false,
      color: "#2dd4bf",
      width: 4,
    };
  }

  if (edge.kind === "branch") {
    return {
      animated: false,
      color: "#fbbf24",
      dash: "7 6",
      width: 3,
    };
  }

  if (edge.traversed) {
    return {
      animated: false,
      color: "#86efac",
      width: 3,
    };
  }

  return {
    animated: false,
    color: "rgba(174, 187, 180, 0.44)",
    dash: "8 7",
    width: 2.4,
  };
}

function toFlowEdges(graph: MissionWallWorkflowGraph | undefined): WorkflowReplayEdge[] {
  const nodeByStepId = new Map(
    (graph?.nodes ?? []).map((node) => [node.stepId, node] as const),
  );
  const nodeById = new Map(
    (graph?.nodes ?? []).map((node) => [node.id, node] as const),
  );
  const resolveFlowNodeId = (stepOrNodeId: string): string | undefined =>
    nodeByStepId.get(stepOrNodeId)?.id ?? nodeById.get(stepOrNodeId)?.id;

  return (graph?.edges ?? []).flatMap((edge) => {
    const source = resolveFlowNodeId(edge.fromStepId);
    const target = resolveFlowNodeId(edge.toStepId);
    if (!source || !target) {
      return [];
    }

    const targetNode = nodeByStepId.get(edge.toStepId) ?? nodeById.get(edge.toStepId);
    const tone = edgeTone(edge, targetNode);

    return [{
      animated: tone.animated,
      className: edge.focused ? "mission-wall-flow-edge--focused" : undefined,
      data: { edge },
      id: edge.id,
      label: edge.branchLabel,
      labelBgBorderRadius: 6,
      labelBgPadding: [8, 4],
      labelBgStyle: {
        fill: "rgba(9, 17, 15, 0.92)",
      },
      labelStyle: {
        fill: tone.color,
        fontSize: 12,
        fontWeight: 760,
      },
      markerEnd: {
        color: tone.color,
        height: 16,
        type: MarkerType.ArrowClosed,
        width: 16,
      },
      source,
      style: {
        filter:
          targetNode?.status === "failed" || edge.focused
            ? `drop-shadow(0 0 10px ${tone.color}66)`
            : undefined,
        stroke: tone.color,
        strokeDasharray: tone.dash,
        strokeWidth: tone.width,
      },
      target,
      type: "smoothstep",
      zIndex: edge.focused || targetNode?.status === "failed" ? 8 : 4,
    }];
  });
}

export function WorkflowReplayCanvas({
  graph,
}: {
  readonly graph?: MissionWallWorkflowGraph;
}) {
  const [flowInstance, setFlowInstance] =
    React.useState<ReactFlowInstance<WorkflowReplayNode, WorkflowReplayEdge> | null>(
      null,
    );
  const nodes = React.useMemo(() => toFlowNodes(graph), [graph]);
  const edges = React.useMemo(() => toFlowEdges(graph), [graph]);
  const focusNodeIds = React.useMemo(() => focusWindowStepIds(graph), [graph]);
  const identityKey = React.useMemo(() => graphIdentityKey(graph), [graph]);
  const viewportKey = React.useMemo(() => graphViewportKey(graph), [graph]);
  const lastFitIdentityKeyRef = React.useRef<string | undefined>(undefined);
  const lastFitKeyRef = React.useRef<string | undefined>(undefined);
  const userMovedViewportRef = React.useRef(false);

  const markUserMovedViewport = React.useCallback(() => {
    userMovedViewportRef.current = true;
  }, []);

  const handleMove: OnMove = React.useCallback((event) => {
    if (event) {
      markUserMovedViewport();
    }
  }, [markUserMovedViewport]);

  const handleNodeDragStart: NodeMouseHandler = React.useCallback(() => {
    markUserMovedViewport();
  }, [markUserMovedViewport]);

  React.useLayoutEffect(() => {
    if (!flowInstance || !nodes.length) {
      return undefined;
    }
    const graphIdentityChanged = lastFitIdentityKeyRef.current !== identityKey;
    if (lastFitKeyRef.current === viewportKey) {
      return undefined;
    }
    if (userMovedViewportRef.current && !graphIdentityChanged) {
      return undefined;
    }

    lastFitIdentityKeyRef.current = identityKey;
    lastFitKeyRef.current = viewportKey;
    userMovedViewportRef.current = false;

    const focusNodes = focusNodeIds.length
      ? nodes.filter((node) => focusNodeIds.includes(node.id))
      : nodes;
    const fitNodes = focusNodes.length ? focusNodes : nodes;

    const fit = () => {
      void flowInstance.fitView({
        duration: 0,
        maxZoom: 1.05,
        minZoom: 0.36,
        nodes: fitNodes,
        padding: 0.24,
      });
    };

    if (typeof window.requestAnimationFrame === "function") {
      const frame = window.requestAnimationFrame(fit);
      return () => window.cancelAnimationFrame(frame);
    }

    fit();
    return undefined;
  }, [flowInstance, focusNodeIds, identityKey, nodes, viewportKey]);

  return (
    <section className="mission-wall-canvas">
      <div className="mission-wall-graph" data-testid="mission-wall-graph">
        <ReactFlow
          className="mission-wall-react-flow"
          edges={edges}
          edgesFocusable={false}
          elementsSelectable={false}
          maxZoom={1.4}
          minZoom={0.28}
          nodeTypes={nodeTypes}
          nodes={nodes}
          nodesConnectable={false}
          nodesDraggable={false}
          nodesFocusable={false}
          onInit={setFlowInstance}
          onMove={handleMove}
          onNodeDragStart={handleNodeDragStart}
          panOnDrag
          proOptions={{ hideAttribution: true }}
          zoomOnDoubleClick={false}
          zoomOnScroll
        >
          <Background
            color="rgba(201, 213, 206, 0.14)"
            gap={34}
            size={1}
            variant={BackgroundVariant.Lines}
          />
          <Controls
            position="bottom-right"
            showInteractive={false}
          />
        </ReactFlow>
      </div>
    </section>
  );
}
