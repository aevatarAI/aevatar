import { type Edge, MarkerType, type Node, Position } from '@xyflow/react';
import {
  reconcileGraphEdges,
  reconcileGraphNodes,
} from '@/shared/graphs/reconcileGraphElements';
import { t } from '@/shared/i18n/messages';
import type {
  WorkflowActivityRunDetail,
  WorkflowActivityRunGraph,
  WorkflowActivityStep,
} from '@/shared/models/workflowActivity';
import type {
  StudioGraphEdgeData,
  StudioGraphNodeData,
} from '@/shared/studio/graph';

export type ExecutionGraphView = {
  readonly edges: Edge<StudioGraphEdgeData>[];
  readonly nodes: Node<StudioGraphNodeData>[];
  readonly orderedSteps: WorkflowActivityStep[];
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

export function getStepExecutionStatus(
  step: WorkflowActivityStep,
): 'idle' | 'active' | 'waiting' | 'completed' | 'failed' {
  if (step.success === true) return 'completed';
  if (step.success === false || trimOptional(step.error)) return 'failed';
  if (trimOptional(step.suspensionType)) return 'waiting';
  if (trimOptional(step.requestedAtUtc) && !trimOptional(step.completedAtUtc)) {
    return 'active';
  }
  return 'idle';
}

export function getStepDisplayName(
  step: WorkflowActivityStep | null | undefined,
): string {
  const stepId = trimOptional(step?.stepId);
  const stepType = trimOptional(step?.stepType);
  return stepId || stepType || t('workflowActivityVNext.run.step', 'Step');
}

function summarizeStepParameters(step: WorkflowActivityStep): string {
  const entries = Object.entries(step.requestParameters).filter(
    ([key, value]) => trimOptional(key) || trimOptional(value),
  );
  if (!entries.length) {
    return step.stepType || t('workflowActivityVNext.run.step', 'step');
  }
  return entries
    .slice(0, 2)
    .map(([key, value]) => `${key}: ${value}`)
    .join(' | ');
}

function getStepSortTimestamp(step: WorkflowActivityStep): number {
  return (
    Date.parse(
      trimOptional(step.requestedAtUtc) || trimOptional(step.completedAtUtc),
    ) || 0
  );
}

export function buildExecutionGraph(
  detail: WorkflowActivityRunDetail | undefined,
  graph: WorkflowActivityRunGraph | undefined,
): ExecutionGraphView {
  const orderedSteps = [...(detail?.steps ?? [])].sort((left, right) => {
    const leftTime = getStepSortTimestamp(left);
    const rightTime = getStepSortTimestamp(right);
    if (leftTime !== rightTime) return leftTime - rightTime;
    return left.stepId.localeCompare(right.stepId);
  });
  const stepById = new Map(
    orderedSteps.map((step) => [step.stepId, step] as const),
  );
  const nodeById = new Map(
    graph?.nodes.map((node) => [node.nodeId, node] as const) ?? [],
  );
  const stepIdByNodeId = new Map(
    graph?.nodes.map(
      (node) => [node.nodeId, trimOptional(node.stepId)] as const,
    ) ?? [],
  );
  const nodes: Node<StudioGraphNodeData>[] = orderedSteps.map(
    (step, index) => ({
      data: {
        branchCount: trimOptional(step.branchKey) ? 1 : 0,
        executionStatus: getStepExecutionStatus(step),
        kind: 'step',
        label: getStepDisplayName(step),
        parametersSummary: summarizeStepParameters(step),
        stepId: step.stepId,
        stepType: step.stepType || 'step',
        subtitle: step.stepType || t('workflowActivityVNext.run.step', 'Step'),
        targetRole: step.targetRole,
        title: getStepDisplayName(step),
      },
      id: `step:${step.stepId}`,
      position: {
        x: 120 + index * 310,
        y: 150 + (index % 2 === 0 ? 0 : 44),
      },
      sourcePosition: Position.Right,
      targetPosition: Position.Left,
      type: 'studioWorkflowNode',
    }),
  );
  const edges: Edge<StudioGraphEdgeData>[] = [];
  const seen = new Set<string>();
  const pushEdge = (
    sourceStepId: string,
    targetStepId: string,
    implicit: boolean,
    branchLabel?: string,
  ) => {
    if (!stepById.has(sourceStepId) || !stepById.has(targetStepId)) return;
    const key = `${sourceStepId}->${targetStepId}:${branchLabel ?? ''}`;
    if (seen.has(key)) return;
    seen.add(key);
    edges.push({
      animated: false,
      data: {
        branchLabel,
        implicit,
        kind: branchLabel ? 'branch' : 'next',
      },
      id: `edge:${sourceStepId}:${targetStepId}:${edges.length}`,
      label: branchLabel || undefined,
      markerEnd: {
        color: implicit ? '#94a3b8' : '#1677ff',
        height: 10,
        type: MarkerType.ArrowClosed,
        width: 10,
      },
      source: `step:${sourceStepId}`,
      style: {
        stroke: implicit ? '#94a3b8' : '#1677ff',
        strokeDasharray: implicit ? '5 5' : undefined,
        strokeWidth: implicit ? 1.6 : 2.4,
      },
      target: `step:${targetStepId}`,
      type: 'smoothstep',
    });
  };

  if (graph?.edges.length) {
    for (const edge of graph.edges) {
      const sourceStepId =
        trimOptional(stepIdByNodeId.get(edge.fromNodeId)) ||
        nodeById.get(edge.fromNodeId)?.stepId ||
        '';
      const targetStepId =
        trimOptional(stepIdByNodeId.get(edge.toNodeId)) ||
        nodeById.get(edge.toNodeId)?.stepId ||
        '';
      if (sourceStepId && targetStepId) {
        pushEdge(
          sourceStepId,
          targetStepId,
          false,
          trimOptional(edge.branchKey) || undefined,
        );
      }
    }
  }

  for (const step of orderedSteps) {
    const nextStepId = trimOptional(step.nextStepId);
    if (nextStepId) {
      pushEdge(
        step.stepId,
        nextStepId,
        false,
        trimOptional(step.branchKey) || undefined,
      );
    }
  }

  if (!edges.length) {
    orderedSteps.forEach((step, index) => {
      const next = orderedSteps[index + 1];
      if (next) {
        pushEdge(step.stepId, next.stepId, true);
      }
    });
  }

  return { edges, nodes, orderedSteps };
}

function haveSameOrderedSteps(
  previous: readonly WorkflowActivityStep[],
  next: readonly WorkflowActivityStep[],
): boolean {
  return (
    previous.length === next.length &&
    previous.every((step, index) => step === next[index])
  );
}

export function reconcileExecutionGraph(
  previous: ExecutionGraphView | undefined,
  next: ExecutionGraphView,
): ExecutionGraphView {
  if (!previous) return next;

  const nodes = reconcileGraphNodes(previous.nodes, next.nodes);
  const edges = reconcileGraphEdges(previous.edges, next.edges);
  const orderedSteps = haveSameOrderedSteps(
    previous.orderedSteps,
    next.orderedSteps,
  )
    ? previous.orderedSteps
    : next.orderedSteps;

  if (
    nodes === previous.nodes &&
    edges === previous.edges &&
    orderedSteps === previous.orderedSteps
  ) {
    return previous;
  }

  return { edges, nodes, orderedSteps };
}
