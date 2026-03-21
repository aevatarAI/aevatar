import {
  MarkerType,
  Position,
  type Edge,
  type Node,
  type XYPosition,
} from '@xyflow/react';

export type StudioGraphRole = {
  readonly id: string;
  readonly name: string;
  readonly systemPrompt: string;
  readonly provider: string;
  readonly model: string;
  readonly connectors: string[];
};

export type StudioGraphStep = {
  readonly id: string;
  readonly type: string;
  readonly targetRole: string;
  readonly parameters: Record<string, unknown>;
  readonly next: string | null;
  readonly branches: Record<string, string>;
};

export type StudioGraphNodeKind = 'step';

export type StudioGraphPrimitiveCategory = {
  readonly key: string;
  readonly label: string;
  readonly color: string;
  readonly items: readonly string[];
};

export type StudioGraphNodeData = {
  readonly label: string;
  readonly kind: StudioGraphNodeKind;
  readonly title: string;
  readonly subtitle: string;
  readonly stepId: string;
  readonly stepType: string;
  readonly targetRole: string;
  readonly parametersSummary: string;
  readonly branchCount: number;
  readonly executionStatus?: 'idle' | 'active' | 'waiting' | 'completed' | 'failed';
  readonly executionFocused?: boolean;
};

export type StudioGraphEdgeData = {
  readonly kind: 'next' | 'branch';
  readonly branchLabel?: string;
  readonly implicit?: boolean;
};

export type StudioGraphElements = {
  readonly roles: StudioGraphRole[];
  readonly steps: StudioGraphStep[];
  readonly nodes: Node<StudioGraphNodeData>[];
  readonly edges: Edge<StudioGraphEdgeData>[];
};

export type StudioWorkflowLayoutDocument = {
  readonly nodePositions?: Record<string, XYPosition>;
  readonly viewport?: {
    readonly x: number;
    readonly y: number;
    readonly zoom: number;
  };
  readonly mode?: string;
  readonly layoutVersion?: number;
  readonly groups?: Record<string, unknown>;
  readonly collapsed?: readonly string[];
  readonly entryWorkflow?: string;
};

type WorkflowDocumentLike = {
  readonly name?: string;
  readonly description?: string;
  readonly roles?: unknown[];
  readonly steps?: unknown[];
};

export const STUDIO_GRAPH_CATEGORIES: readonly StudioGraphPrimitiveCategory[] = [
  {
    key: 'data',
    label: 'Data',
    color: '#3B82F6',
    items: ['transform', 'assign', 'retrieve_facts', 'cache'],
  },
  {
    key: 'control',
    label: 'Control',
    color: '#8B5CF6',
    items: ['guard', 'conditional', 'switch', 'while', 'delay', 'wait_signal', 'checkpoint'],
  },
  {
    key: 'ai',
    label: 'AI',
    color: '#EC4899',
    items: ['llm_call', 'tool_call', 'evaluate', 'reflect'],
  },
  {
    key: 'composition',
    label: 'Composition',
    color: '#F59E0B',
    items: ['foreach', 'parallel', 'race', 'map_reduce', 'workflow_call', 'dynamic_workflow', 'vote'],
  },
  {
    key: 'integration',
    label: 'Integration',
    color: '#10B981',
    items: ['connector_call', 'emit'],
  },
  {
    key: 'human',
    label: 'Human',
    color: '#06B6D4',
    items: ['human_input', 'human_approval'],
  },
  {
    key: 'validation',
    label: 'Validation',
    color: '#64748B',
    items: ['workflow_yaml_validate'],
  },
];

function normalizeString(value: unknown): string {
  return String(value ?? '').trim();
}

function normalizeConnectors(value: unknown): string[] {
  return Array.isArray(value)
    ? value
        .map((item) => normalizeString(item))
        .filter(Boolean)
    : [];
}

function normalizeParameters(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return {};
  }

  return Object.fromEntries(Object.entries(value));
}

function normalizeBranches(value: unknown): Record<string, string> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return {};
  }

  return Object.fromEntries(
    Object.entries(value)
      .map(([label, target]) => [normalizeString(label), normalizeString(target)])
      .filter(([label, target]) => Boolean(label) && Boolean(target)),
  );
}

function compareBranchLabels(left: string, right: string): number {
  const rank = (value: string) => {
    const normalized = normalizeString(value).toLowerCase();
    if (normalized === 'true') return 0;
    if (normalized === 'false') return 1;
    if (normalized === '_default' || normalized === 'default') return 2;
    return 3;
  };

  const rankDifference = rank(left) - rank(right);
  if (rankDifference !== 0) {
    return rankDifference;
  }

  return left.localeCompare(right);
}

function normalizeRoles(document: WorkflowDocumentLike): StudioGraphRole[] {
  if (!Array.isArray(document.roles)) {
    return [];
  }

  return document.roles
    .map((role) => {
      const payload = role && typeof role === 'object' ? role : {};
      const id = normalizeString((payload as { id?: unknown }).id);
      if (!id) {
        return null;
      }

      return {
        id,
        name: normalizeString((payload as { name?: unknown }).name) || id,
        systemPrompt: normalizeString(
          (payload as { systemPrompt?: unknown }).systemPrompt,
        ),
        provider: normalizeString((payload as { provider?: unknown }).provider),
        model: normalizeString((payload as { model?: unknown }).model),
        connectors: normalizeConnectors(
          (payload as { connectors?: unknown }).connectors,
        ),
      } satisfies StudioGraphRole;
    })
    .filter((role): role is StudioGraphRole => Boolean(role));
}

function normalizeSteps(document: WorkflowDocumentLike): StudioGraphStep[] {
  if (!Array.isArray(document.steps)) {
    return [];
  }

  return document.steps
    .map((step) => {
      const payload = step && typeof step === 'object' ? step : {};
      const id = normalizeString((payload as { id?: unknown }).id);
      if (!id) {
        return null;
      }

      const type =
        normalizeString((payload as { type?: unknown }).type) ||
        normalizeString((payload as { originalType?: unknown }).originalType) ||
        'step';
      const targetRole =
        normalizeString((payload as { targetRole?: unknown }).targetRole) ||
        normalizeString((payload as { target_role?: unknown }).target_role);

      return {
        id,
        type,
        targetRole,
        parameters: normalizeParameters(
          (payload as { parameters?: unknown }).parameters,
        ),
        next: normalizeString((payload as { next?: unknown }).next) || null,
        branches: normalizeBranches(
          (payload as { branches?: unknown }).branches,
        ),
      } satisfies StudioGraphStep;
    })
    .filter((step): step is StudioGraphStep => Boolean(step));
}

function summarizeStepParameters(parameters: Record<string, unknown>): string {
  const entries = Object.entries(parameters);
  if (entries.length === 0) {
    return 'No parameters configured';
  }

  return entries
    .slice(0, 2)
    .map(([key, value]) => {
      if (typeof value === 'string') {
        return `${key}: ${value}`;
      }

      if (
        typeof value === 'number' ||
        typeof value === 'boolean' ||
        value === null
      ) {
        return `${key}: ${String(value)}`;
      }

      return `${key}: ${JSON.stringify(value)}`;
    })
    .join(' · ');
}

function extractSavedLayoutPositions(
  layout: unknown,
): Record<string, XYPosition> {
  if (!layout || typeof layout !== 'object') {
    return {};
  }

  const nodePositions = (layout as StudioWorkflowLayoutDocument).nodePositions;
  if (!nodePositions || typeof nodePositions !== 'object') {
    return {};
  }

  return Object.fromEntries(
    Object.entries(nodePositions)
      .map(([stepId, position]) => {
        const x =
          typeof position?.x === 'number' && Number.isFinite(position.x)
            ? position.x
            : null;
        const y =
          typeof position?.y === 'number' && Number.isFinite(position.y)
            ? position.y
            : null;
        if (!stepId || x === null || y === null) {
          return null;
        }

        return [stepId, { x, y }] as const;
      })
      .filter((entry): entry is readonly [string, XYPosition] => Boolean(entry)),
  );
}

function buildAutoLayoutPositions(
  steps: StudioGraphStep[],
): Record<string, XYPosition> {
  const validStepIds = new Set(steps.map((step) => step.id).filter(Boolean));
  if (validStepIds.size === 0) {
    return {};
  }

  const outgoing = new Map<string, string[]>();
  const incomingCount = new Map<string, number>();
  for (const stepId of validStepIds) {
    outgoing.set(stepId, []);
    incomingCount.set(stepId, 0);
  }

  steps.forEach((step, index) => {
    const nextTargets: string[] = [];
    if (step.next && validStepIds.has(step.next)) {
      nextTargets.push(step.next);
    } else if (
      !step.next &&
      Object.keys(step.branches).length === 0 &&
      index < steps.length - 1
    ) {
      const fallbackNext = steps[index + 1]?.id;
      if (fallbackNext && validStepIds.has(fallbackNext)) {
        nextTargets.push(fallbackNext);
      }
    }

    const branchTargets = Object.entries(step.branches)
      .sort(([left], [right]) => compareBranchLabels(left, right))
      .map(([, targetStepId]) => targetStepId)
      .filter((targetStepId) => targetStepId && validStepIds.has(targetStepId));

    for (const targetStepId of [...nextTargets, ...branchTargets]) {
      const currentTargets = outgoing.get(step.id) ?? [];
      if (!currentTargets.includes(targetStepId)) {
        currentTargets.push(targetStepId);
        outgoing.set(step.id, currentTargets);
        incomingCount.set(
          targetStepId,
          (incomingCount.get(targetStepId) ?? 0) + 1,
        );
      }
    }
  });

  const treeChildren = new Map<string, string[]>();
  const depths = new Map<string, number>();
  const visited = new Set<string>();
  const rootOrder = new Set<string>();
  const firstStepId = steps[0]?.id ?? '';
  if (firstStepId) {
    rootOrder.add(firstStepId);
  }

  for (const step of steps) {
    if ((incomingCount.get(step.id) ?? 0) === 0) {
      rootOrder.add(step.id);
    }
  }

  for (const step of steps) {
    rootOrder.add(step.id);
  }

  function visit(stepId: string, depth: number) {
    if (visited.has(stepId)) {
      return;
    }

    visited.add(stepId);
    depths.set(stepId, depth);
    const children = outgoing.get(stepId) ?? [];
    const treeTargets: string[] = [];
    for (const childId of children) {
      if (visited.has(childId)) {
        continue;
      }

      treeTargets.push(childId);
      visit(childId, depth + 1);
    }
    treeChildren.set(stepId, treeTargets);
  }

  for (const rootId of rootOrder) {
    if (!visited.has(rootId)) {
      visit(rootId, 0);
    }
  }

  const subtreeSize = new Map<string, number>();
  function measure(stepId: string): number {
    const children = treeChildren.get(stepId) ?? [];
    if (children.length === 0) {
      subtreeSize.set(stepId, 1);
      return 1;
    }

    const total = children.reduce((sum, childId) => sum + measure(childId), 0);
    const size = Math.max(1, total);
    subtreeSize.set(stepId, size);
    return size;
  }

  for (const rootId of rootOrder) {
    if (depths.has(rootId) && !subtreeSize.has(rootId)) {
      measure(rootId);
    }
  }

  const positions: Record<string, XYPosition> = {};
  let globalRow = 0;

  function place(stepId: string, startRow: number) {
    const children = treeChildren.get(stepId) ?? [];
    const size = subtreeSize.get(stepId) ?? 1;
    const depth = depths.get(stepId) ?? 0;
    const centerRow = startRow + (size - 1) / 2;
    positions[stepId] = {
      x: 240 + depth * 330,
      y: 180 + centerRow * 200,
    };

    let nextRow = startRow;
    for (const childId of children) {
      const childSize = subtreeSize.get(childId) ?? 1;
      place(childId, nextRow);
      nextRow += childSize;
    }
  }

  for (const rootId of rootOrder) {
    if (!depths.has(rootId) || positions[rootId]) {
      continue;
    }

    place(rootId, globalRow);
    globalRow += (subtreeSize.get(rootId) ?? 1) + 0.8;
  }

  return positions;
}

export function getStudioGraphCategory(
  type: string,
): StudioGraphPrimitiveCategory {
  return (
    STUDIO_GRAPH_CATEGORIES.find((category) => category.items.includes(type)) ?? {
      key: 'custom',
      label: 'Custom',
      color: '#6B7280',
      items: [],
    }
  );
}

export function buildStudioWorkflowLayout(
  workflowName: string,
  nodes: Node<StudioGraphNodeData>[],
  previousLayout?: unknown,
): StudioWorkflowLayoutDocument {
  const viewport =
    previousLayout &&
    typeof previousLayout === 'object' &&
    (previousLayout as StudioWorkflowLayoutDocument).viewport
      ? (previousLayout as StudioWorkflowLayoutDocument).viewport
      : { x: 0, y: 0, zoom: 1 };

  return {
    nodePositions: Object.fromEntries(
      nodes
        .filter((node) => node.data?.stepId)
        .map((node) => [
          node.data.stepId,
          {
            x: node.position.x,
            y: node.position.y,
          },
        ]),
    ),
    viewport,
    mode: 'manual',
    layoutVersion: 2,
    groups: {},
    collapsed: [],
    entryWorkflow: normalizeString(workflowName) || 'draft',
  };
}

export function buildStudioGraphElements(
  document: unknown,
  layout?: unknown,
): StudioGraphElements {
  const normalizedDocument =
    document && typeof document === 'object'
      ? (document as WorkflowDocumentLike)
      : {};
  const roles = normalizeRoles(normalizedDocument);
  const steps = normalizeSteps(normalizedDocument);
  const savedLayoutPositions = extractSavedLayoutPositions(layout);
  const autoLayoutPositions = buildAutoLayoutPositions(steps);

  const nodes: Node<StudioGraphNodeData>[] = steps.map((step, index) => {
    const position = savedLayoutPositions[step.id] ?? autoLayoutPositions[step.id];
    return {
      id: `step:${step.id}`,
      type: 'studioWorkflowNode',
      position: {
        x:
          typeof position?.x === 'number' && Number.isFinite(position.x)
            ? position.x
            : 240 + index * 330,
        y:
          typeof position?.y === 'number' && Number.isFinite(position.y)
            ? position.y
            : 180,
      },
      sourcePosition: Position.Right,
      targetPosition: Position.Left,
      data: {
        label: step.id,
        kind: 'step',
        title: step.id,
        subtitle: step.type,
        stepId: step.id,
        stepType: step.type,
        targetRole: step.targetRole,
        parametersSummary: summarizeStepParameters(step.parameters),
        branchCount: Object.keys(step.branches).length,
      },
    };
  });

  const stepNodeById = new Map(
    nodes.map((node) => [node.data.stepId, node] as const),
  );
  const edges: Edge<StudioGraphEdgeData>[] = [];
  steps.forEach((step, index) => {
    const sourceNode = stepNodeById.get(step.id);
    if (!sourceNode) {
      return;
    }

    if (step.next && stepNodeById.has(step.next)) {
      const targetNode = stepNodeById.get(step.next);
      if (targetNode) {
        edges.push({
          id: `edge:${step.id}:${step.next}:next`,
          source: sourceNode.id,
          target: targetNode.id,
          type: 'smoothstep',
          label: undefined,
          animated: false,
          data: {
            kind: 'next',
            implicit: false,
          },
          style: {
            stroke: '#2F6FEC',
            strokeWidth: 2.5,
          },
          markerEnd: {
            type: MarkerType.ArrowClosed,
            width: 11,
            height: 11,
            color: '#2F6FEC',
          },
          zIndex: 4,
        });
      }
    } else if (Object.keys(step.branches).length === 0 && index < steps.length - 1) {
      const fallbackTarget = steps[index + 1]?.id;
      const targetNode = fallbackTarget ? stepNodeById.get(fallbackTarget) : null;
      if (targetNode) {
        edges.push({
          id: `edge:${step.id}:${fallbackTarget}:next`,
          source: sourceNode.id,
          target: targetNode.id,
          type: 'smoothstep',
          data: {
            kind: 'next',
            implicit: true,
          },
          style: {
            stroke: '#2F6FEC',
            strokeWidth: 2.5,
          },
          markerEnd: {
            type: MarkerType.ArrowClosed,
            width: 11,
            height: 11,
            color: '#2F6FEC',
          },
          zIndex: 4,
        });
      }
    }

    Object.entries(step.branches)
      .sort(([left], [right]) => compareBranchLabels(left, right))
      .forEach(([branchLabel, targetStepId]) => {
        const targetNode = stepNodeById.get(targetStepId);
        if (!targetNode) {
          return;
        }

        edges.push({
          id: `edge:${step.id}:${targetStepId}:${branchLabel}`,
          source: sourceNode.id,
          target: targetNode.id,
          type: 'smoothstep',
          label: branchLabel,
          animated: false,
          data: {
            kind: 'branch',
            branchLabel,
            implicit: false,
          },
          style: {
            stroke: '#8B5CF6',
            strokeWidth: 2.5,
          },
          markerEnd: {
            type: MarkerType.ArrowClosed,
            width: 11,
            height: 11,
            color: '#8B5CF6',
          },
          zIndex: 4,
          labelStyle: {
            fill: '#6B7280',
            fontSize: 12,
          },
        });
      });
  });

  return {
    roles,
    steps,
    nodes,
    edges,
  };
}
