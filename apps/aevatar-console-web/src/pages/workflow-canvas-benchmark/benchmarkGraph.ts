import type { Edge, Node } from '@xyflow/react';
import type {
  StudioGraphEdgeData,
  StudioGraphNodeData,
} from '@/shared/studio/graph';

export const WORKFLOW_CANVAS_BENCHMARK_GRAPH_SIZES = [100, 500, 1000] as const;
export const WORKFLOW_CANVAS_BENCHMARK_SCENARIOS = [
  'initial-load',
  'drag',
  'selection',
  'pan',
  'zoom-same-band',
  'zoom-threshold',
  'status-update',
  'topology-add',
] as const;

export type WorkflowCanvasBenchmarkGraphSize =
  (typeof WORKFLOW_CANVAS_BENCHMARK_GRAPH_SIZES)[number];
export type WorkflowCanvasBenchmarkScenario =
  (typeof WORKFLOW_CANVAS_BENCHMARK_SCENARIOS)[number];

export type WorkflowCanvasBenchmarkPolicy = {
  readonly minimap: boolean;
  readonly visibleElementsOnly: boolean;
};

export type WorkflowCanvasBenchmarkResult = {
  readonly buildMode: 'production';
  readonly browser: string;
  readonly graph: {
    readonly nodes: WorkflowCanvasBenchmarkGraphSize;
    readonly edges: number;
  };
  readonly policy: WorkflowCanvasBenchmarkPolicy;
  readonly scenario: WorkflowCanvasBenchmarkScenario;
  readonly durationMs: number;
  readonly longTasks: number;
  readonly renderedNodeCount: number;
  readonly changedNodeReferences: number;
  readonly usedHeapBytes: number | null;
};

export type WorkflowCanvasBenchmarkReactCommit = {
  readonly actualDurationMs: number;
  readonly phase: 'mount' | 'nested-update' | 'update';
  readonly startTimeMs: number;
};

export type WorkflowCanvasBenchmarkMeasurement = {
  readonly changedNodeReferences: number;
  readonly longTasks: number;
  readonly reactCommits?: readonly WorkflowCanvasBenchmarkReactCommit[];
  readonly renderedNodeCount: number;
  readonly usedHeapBytes: number | null;
};

export type WorkflowCanvasBenchmarkGraph = {
  readonly nodes: readonly Node<StudioGraphNodeData>[];
  readonly edges: readonly Edge<StudioGraphEdgeData>[];
};

export type WorkflowCanvasBenchmarkProgress = {
  readonly complete: boolean;
  readonly markdownLines: readonly [string, string];
};

const BENCHMARK_COLUMNS = 40;
const BENCHMARK_COLUMN_PITCH = 340;
const BENCHMARK_NODE_HEIGHT = 120;
const BENCHMARK_NODE_WIDTH = 268;
const BENCHMARK_ROW_PITCH = 190;
const BENCHMARK_STEP_TYPES = [
  ['llm_call', 'LLM call'],
  ['tool_call', 'Tool call'],
  ['conditional', 'Conditional'],
  ['transform', 'Transform'],
  ['connector_call', 'Connector call'],
  ['human_approval', 'Human approval'],
  ['workflow_call', 'Workflow call'],
  ['emit', 'Emit'],
] as const;
const BENCHMARK_ROLES = [
  'coordinator',
  'researcher',
  'reviewer',
  'publisher',
] as const;

function benchmarkNodeId(index: number): string {
  return `benchmark-node-${String(index + 1).padStart(4, '0')}`;
}

function createBenchmarkNode(
  index: number,
  graphSize: number,
): Node<StudioGraphNodeData> {
  const nodeId = benchmarkNodeId(index);
  const [stepType, subtitle] =
    BENCHMARK_STEP_TYPES[index % BENCHMARK_STEP_TYPES.length];
  const targetRole = BENCHMARK_ROLES[index % BENCHMARK_ROLES.length];

  return {
    id: nodeId,
    initialHeight: BENCHMARK_NODE_HEIGHT,
    initialWidth: BENCHMARK_NODE_WIDTH,
    data: {
      branchCount: index + 2 < graphSize ? 1 : 0,
      executionFocused: false,
      executionStatus: 'idle',
      kind: 'step',
      label: `Benchmark step ${index + 1}`,
      parametersSummary: `role: ${targetRole}; sequence: ${index + 1}`,
      stepId: nodeId,
      stepType,
      subtitle,
      targetRole,
      title: `Benchmark step ${index + 1}`,
    },
    position: {
      x: (index % BENCHMARK_COLUMNS) * BENCHMARK_COLUMN_PITCH,
      y: Math.floor(index / BENCHMARK_COLUMNS) * BENCHMARK_ROW_PITCH,
    },
    type: 'studioWorkflowNode',
  };
}

function createBenchmarkEdge(
  sourceIndex: number,
  targetIndex: number,
  kind: StudioGraphEdgeData['kind'],
  branchLabel?: string,
): Edge<StudioGraphEdgeData> {
  const source = benchmarkNodeId(sourceIndex);
  const target = benchmarkNodeId(targetIndex);
  return {
    id: `benchmark-edge-${sourceIndex + 1}-${targetIndex + 1}-${kind}`,
    source,
    target,
    type: 'smoothstep',
    data: {
      branchLabel,
      kind,
    },
  };
}

export function parseWorkflowCanvasBenchmarkGraphSize(
  value: string | null,
): WorkflowCanvasBenchmarkGraphSize {
  const parsed = Number(value);
  if (
    value === null ||
    !WORKFLOW_CANVAS_BENCHMARK_GRAPH_SIZES.includes(
      parsed as WorkflowCanvasBenchmarkGraphSize,
    ) ||
    String(parsed) !== value
  ) {
    throw new Error(
      `Unsupported workflow canvas benchmark graph size: ${value ?? 'missing'}`,
    );
  }

  return parsed as WorkflowCanvasBenchmarkGraphSize;
}

export function createWorkflowCanvasBenchmarkGraph(
  size: WorkflowCanvasBenchmarkGraphSize,
): WorkflowCanvasBenchmarkGraph {
  if (!WORKFLOW_CANVAS_BENCHMARK_GRAPH_SIZES.includes(size)) {
    throw new Error(
      `Unsupported workflow canvas benchmark graph size: ${size}`,
    );
  }

  const nodes = Array.from({ length: size }, (_, index) =>
    createBenchmarkNode(index, size),
  );
  const edges: Edge<StudioGraphEdgeData>[] = [];
  nodes.forEach((_, index) => {
    if (index + 1 < size) {
      edges.push(createBenchmarkEdge(index, index + 1, 'next'));
    }
    if (index + 2 < size) {
      edges.push(createBenchmarkEdge(index, index + 2, 'branch', 'alternate'));
    }
  });

  return { edges, nodes };
}

export function addWorkflowCanvasBenchmarkTopology(
  graph: WorkflowCanvasBenchmarkGraph,
): WorkflowCanvasBenchmarkGraph {
  const newNodeIndex = graph.nodes.length;
  const branchSourceIndex = newNodeIndex - 2;
  const nextSourceIndex = newNodeIndex - 1;
  const nodes = graph.nodes.map((node, index) =>
    index === branchSourceIndex
      ? {
          ...node,
          data: { ...node.data, branchCount: node.data.branchCount + 1 },
        }
      : node,
  );
  nodes.push(createBenchmarkNode(newNodeIndex, newNodeIndex + 1));

  return {
    nodes,
    edges: [
      ...graph.edges,
      createBenchmarkEdge(
        branchSourceIndex,
        newNodeIndex,
        'branch',
        'topology-add',
      ),
      createBenchmarkEdge(nextSourceIndex, newNodeIndex, 'next'),
    ],
  };
}

export function countChangedNodeReferences(
  previousNodes: readonly Node[],
  nextNodes: readonly Node[],
): number {
  return getChangedNodeReferenceIds(previousNodes, nextNodes).size;
}

export function getChangedNodeReferenceIds(
  previousNodes: readonly Node[],
  nextNodes: readonly Node[],
): ReadonlySet<string> {
  const previousById = new Map(previousNodes.map((node) => [node.id, node]));
  const nextIds = new Set(nextNodes.map((node) => node.id));
  const changedNodeIds = new Set(
    nextNodes
      .filter((node) => previousById.get(node.id) !== node)
      .map((node) => node.id),
  );
  for (const node of previousNodes) {
    if (!nextIds.has(node.id)) {
      changedNodeIds.add(node.id);
    }
  }
  return changedNodeIds;
}

export function createWorkflowCanvasBenchmarkProgress(
  resultCount: number,
  expectedResultCount: number,
): WorkflowCanvasBenchmarkProgress {
  const complete = resultCount === expectedResultCount;
  return {
    complete,
    markdownLines: [
      `- Complete: ${complete ? 'yes' : 'no'}`,
      `- Results: ${resultCount}/${expectedResultCount}`,
    ],
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

function hasExactKeys(
  value: Record<string, unknown>,
  expectedKeys: readonly string[],
): boolean {
  const actualKeys = Object.keys(value).sort();
  return (
    actualKeys.length === expectedKeys.length &&
    actualKeys.every((key, index) => key === [...expectedKeys].sort()[index])
  );
}

function isNonNegativeFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0;
}

function isNonNegativeInteger(value: unknown): value is number {
  return Number.isInteger(value) && isNonNegativeFiniteNumber(value);
}

export function assertWorkflowCanvasBenchmarkResult(
  value: unknown,
): WorkflowCanvasBenchmarkResult {
  const invalid = () => {
    throw new Error('Invalid workflow canvas benchmark result');
  };
  if (
    !isRecord(value) ||
    !hasExactKeys(value, [
      'browser',
      'buildMode',
      'changedNodeReferences',
      'durationMs',
      'graph',
      'longTasks',
      'policy',
      'renderedNodeCount',
      'scenario',
      'usedHeapBytes',
    ])
  ) {
    return invalid();
  }

  const graph = value.graph;
  const policy = value.policy;
  if (
    value.buildMode !== 'production' ||
    typeof value.browser !== 'string' ||
    !value.browser.trim() ||
    !isRecord(graph) ||
    !hasExactKeys(graph, ['edges', 'nodes']) ||
    !WORKFLOW_CANVAS_BENCHMARK_GRAPH_SIZES.includes(
      graph.nodes as WorkflowCanvasBenchmarkGraphSize,
    ) ||
    !isNonNegativeInteger(graph.edges) ||
    !isRecord(policy) ||
    !hasExactKeys(policy, ['minimap', 'visibleElementsOnly']) ||
    typeof policy.minimap !== 'boolean' ||
    typeof policy.visibleElementsOnly !== 'boolean' ||
    !WORKFLOW_CANVAS_BENCHMARK_SCENARIOS.includes(
      value.scenario as WorkflowCanvasBenchmarkScenario,
    ) ||
    !isNonNegativeFiniteNumber(value.durationMs) ||
    !isNonNegativeInteger(value.longTasks) ||
    !isNonNegativeInteger(value.renderedNodeCount) ||
    !isNonNegativeInteger(value.changedNodeReferences) ||
    (value.usedHeapBytes !== null &&
      !isNonNegativeFiniteNumber(value.usedHeapBytes))
  ) {
    return invalid();
  }

  return value as WorkflowCanvasBenchmarkResult;
}
