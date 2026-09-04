import type { Node } from '@xyflow/react';
import React from 'react';
import GraphCanvas from '@/shared/graphs/GraphCanvas';
import {
  addWorkflowCanvasBenchmarkTopology,
  assertWorkflowCanvasBenchmarkResult,
  countChangedNodeReferences,
  createWorkflowCanvasBenchmarkGraph,
  parseWorkflowCanvasBenchmarkGraphSize,
  WORKFLOW_CANVAS_BENCHMARK_SCENARIOS,
  type WorkflowCanvasBenchmarkGraph,
  type WorkflowCanvasBenchmarkGraphSize,
  type WorkflowCanvasBenchmarkMeasurement,
  type WorkflowCanvasBenchmarkPolicy,
  type WorkflowCanvasBenchmarkReactCommit,
  type WorkflowCanvasBenchmarkResult,
  type WorkflowCanvasBenchmarkScenario,
} from './benchmarkGraph';

type BenchmarkPageConfiguration = {
  readonly browser: string;
  readonly graph: {
    readonly edges: number;
    readonly nodes: WorkflowCanvasBenchmarkGraphSize;
  };
  readonly policy: WorkflowCanvasBenchmarkPolicy;
};

type WorkflowCanvasBenchmarkPageApi = {
  readonly buildMode: 'production';
  readonly config: BenchmarkPageConfiguration;
  appendResult(value: unknown): WorkflowCanvasBenchmarkResult;
  beginScenario(scenario: WorkflowCanvasBenchmarkScenario): void;
  captureMeasurement(): WorkflowCanvasBenchmarkMeasurement;
  getResults(): readonly WorkflowCanvasBenchmarkResult[];
  runStateScenario(scenario: 'status-update' | 'topology-add'): Promise<void>;
};

declare global {
  interface Window {
    __AEVATAR_WORKFLOW_CANVAS_BENCHMARK__?: WorkflowCanvasBenchmarkPageApi;
  }
}

type ActiveMeasurement = {
  changedNodeReferences: number;
  readonly reactCommits: WorkflowCanvasBenchmarkReactCommit[];
  renderedNodeCount: number;
  readonly scenario: WorkflowCanvasBenchmarkScenario;
  readonly startedAt: number;
};

type LongTaskSample = {
  readonly startTime: number;
};

type ChromiumPerformance = Performance & {
  readonly memory?: {
    readonly usedJSHeapSize?: number;
  };
};

const benchmarkPageCss = `
html,
body,
#root {
  height: 100%;
  margin: 0;
  min-height: 0;
  overflow: hidden;
  width: 100%;
}

.workflow-canvas-benchmark {
  background: #f7f9fc;
  height: 100vh;
  min-height: 0;
  overflow: hidden;
  width: 100vw;
}

.workflow-canvas-benchmark > div {
  border: 0 !important;
  border-radius: 0 !important;
}
`;

function parsePolicyFlag(name: string, value: string | null): boolean {
  if (value === '1') {
    return true;
  }
  if (value === '0') {
    return false;
  }
  throw new Error(`Unsupported workflow canvas benchmark ${name}: ${value}`);
}

function readPageConfiguration(): {
  readonly graph: WorkflowCanvasBenchmarkGraph;
  readonly size: WorkflowCanvasBenchmarkGraphSize;
  readonly policy: WorkflowCanvasBenchmarkPolicy;
} {
  const parameters = new URLSearchParams(window.location.search);
  const size = parseWorkflowCanvasBenchmarkGraphSize(parameters.get('nodes'));
  const policy = {
    minimap: parsePolicyFlag('minimap policy', parameters.get('minimap')),
    visibleElementsOnly: parsePolicyFlag(
      'visible-elements policy',
      parameters.get('visible'),
    ),
  };
  return {
    graph: createWorkflowCanvasBenchmarkGraph(size),
    policy,
    size,
  };
}

function afterNextPaint(): Promise<void> {
  return new Promise((resolve) => {
    window.requestAnimationFrame(() => {
      window.requestAnimationFrame(() => resolve());
    });
  });
}

function readUsedHeapBytes(): number | null {
  const usedHeapBytes = (performance as ChromiumPerformance).memory
    ?.usedJSHeapSize;
  return typeof usedHeapBytes === 'number' && Number.isFinite(usedHeapBytes)
    ? usedHeapBytes
    : null;
}

function InvalidBenchmarkConfiguration({ message }: { message: string }) {
  return (
    <main aria-label="Workflow canvas benchmark" role="alert">
      {message}
    </main>
  );
}

function BenchmarkCanvas({
  initialGraph,
  policy,
  size,
}: {
  readonly initialGraph: WorkflowCanvasBenchmarkGraph;
  readonly policy: WorkflowCanvasBenchmarkPolicy;
  readonly size: WorkflowCanvasBenchmarkGraphSize;
}) {
  const [graph, setGraph] = React.useState(initialGraph);
  const [selectedNodeId, setSelectedNodeId] = React.useState<string>();
  const graphRef = React.useRef(graph);
  const renderedNodesRef = React.useRef<readonly Node[] | undefined>(undefined);
  const resultsRef = React.useRef<WorkflowCanvasBenchmarkResult[]>([]);
  const longTasksRef = React.useRef<LongTaskSample[]>([]);
  const measurementRef = React.useRef<ActiveMeasurement>({
    changedNodeReferences: size,
    reactCommits: [],
    renderedNodeCount: 0,
    scenario: 'initial-load',
    startedAt: 0,
  });
  graphRef.current = graph;

  React.useEffect(() => {
    if (
      typeof PerformanceObserver === 'undefined' ||
      !PerformanceObserver.supportedEntryTypes?.includes('longtask')
    ) {
      return undefined;
    }

    const observer = new PerformanceObserver((entryList) => {
      entryList.getEntries().forEach((entry) => {
        longTasksRef.current.push({ startTime: entry.startTime });
      });
    });
    observer.observe({ buffered: true, type: 'longtask' });
    return () => observer.disconnect();
  }, []);

  const handleStudioNodeRender = React.useCallback((_nodeId: string) => {
    measurementRef.current.renderedNodeCount += 1;
  }, []);

  const handleRenderedNodesChange = React.useCallback(
    (renderedNodes: readonly Node[]) => {
      const previousRenderedNodes = renderedNodesRef.current;
      renderedNodesRef.current = renderedNodes;
      if (previousRenderedNodes) {
        measurementRef.current.changedNodeReferences =
          countChangedNodeReferences(previousRenderedNodes, renderedNodes);
      }
    },
    [],
  );

  const handleProfileRender = React.useCallback<React.ProfilerOnRenderCallback>(
    (_id, phase, actualDuration, _baseDuration, startTime) => {
      if (
        Number.isFinite(actualDuration) &&
        actualDuration >= 0 &&
        Number.isFinite(startTime) &&
        startTime >= 0
      ) {
        measurementRef.current.reactCommits.push({
          actualDurationMs: actualDuration,
          phase,
          startTimeMs: startTime,
        });
      }
    },
    [],
  );

  const handleNodeSelect = React.useCallback((nodeId: string) => {
    setSelectedNodeId(nodeId);
  }, []);

  const handleNodeLayoutChange = React.useCallback((renderedNodes: Node[]) => {
    const renderedById = new Map(renderedNodes.map((node) => [node.id, node]));
    setGraph((currentGraph) => {
      const nodes = currentGraph.nodes.map((node) => {
        const renderedNode = renderedById.get(node.id);
        if (
          !renderedNode ||
          (renderedNode.position.x === node.position.x &&
            renderedNode.position.y === node.position.y)
        ) {
          return node;
        }
        return { ...node, position: { ...renderedNode.position } };
      });
      return { ...currentGraph, nodes };
    });
  }, []);

  const beginScenario = React.useCallback(
    (scenario: WorkflowCanvasBenchmarkScenario) => {
      if (!WORKFLOW_CANVAS_BENCHMARK_SCENARIOS.includes(scenario)) {
        throw new Error(`Unsupported workflow canvas scenario: ${scenario}`);
      }
      measurementRef.current = {
        changedNodeReferences: 0,
        reactCommits: [],
        renderedNodeCount: 0,
        scenario,
        startedAt: performance.now(),
      };
    },
    [],
  );

  const captureMeasurement =
    React.useCallback((): WorkflowCanvasBenchmarkMeasurement => {
      const measurement = measurementRef.current;
      const reactCommits = measurement.reactCommits.length
        ? [...measurement.reactCommits]
        : undefined;
      return {
        changedNodeReferences: measurement.changedNodeReferences,
        longTasks: longTasksRef.current.filter(
          (sample) => sample.startTime >= measurement.startedAt,
        ).length,
        reactCommits,
        renderedNodeCount: measurement.renderedNodeCount,
        usedHeapBytes: readUsedHeapBytes(),
      };
    }, []);

  const runStateScenario = React.useCallback(
    async (scenario: 'status-update' | 'topology-add') => {
      if (scenario === 'status-update') {
        setGraph((currentGraph) => {
          const nodes = currentGraph.nodes.map((node, index) =>
            index === 0
              ? {
                  ...node,
                  data: { ...node.data, executionStatus: 'active' as const },
                }
              : node,
          );
          return { ...currentGraph, nodes };
        });
      } else if (scenario === 'topology-add') {
        setGraph((currentGraph) =>
          addWorkflowCanvasBenchmarkTopology(currentGraph),
        );
      } else {
        throw new Error(`Unsupported state scenario: ${scenario}`);
      }
      await afterNextPaint();
    },
    [],
  );

  const browser = window.navigator.userAgent;
  const pageApi = React.useMemo<WorkflowCanvasBenchmarkPageApi>(
    () => ({
      buildMode: 'production',
      config: {
        browser,
        graph: { edges: initialGraph.edges.length, nodes: size },
        policy,
      },
      appendResult(value) {
        const result = assertWorkflowCanvasBenchmarkResult(value);
        if (
          result.browser !== browser ||
          result.graph.nodes !== size ||
          result.graph.edges !== initialGraph.edges.length ||
          result.policy.minimap !== policy.minimap ||
          result.policy.visibleElementsOnly !== policy.visibleElementsOnly
        ) {
          throw new Error(
            'Benchmark result does not match the active page configuration',
          );
        }
        resultsRef.current.push(result);
        return result;
      },
      beginScenario,
      captureMeasurement,
      getResults: () => [...resultsRef.current],
      runStateScenario,
    }),
    [
      beginScenario,
      browser,
      captureMeasurement,
      initialGraph.edges.length,
      policy,
      runStateScenario,
      size,
    ],
  );

  React.useEffect(() => {
    window.__AEVATAR_WORKFLOW_CANVAS_BENCHMARK__ = pageApi;
    return () => {
      if (window.__AEVATAR_WORKFLOW_CANVAS_BENCHMARK__ === pageApi) {
        delete window.__AEVATAR_WORKFLOW_CANVAS_BENCHMARK__;
      }
    };
  }, [pageApi]);

  return (
    <main
      aria-label="Workflow canvas benchmark"
      className="workflow-canvas-benchmark"
      data-benchmark-ready="true"
      data-graph-size={size}
      data-minimap={String(policy.minimap)}
      data-visible-elements-only={String(policy.visibleElementsOnly)}
    >
      <style>{benchmarkPageCss}</style>
      <React.Profiler id="workflow-canvas" onRender={handleProfileRender}>
        <GraphCanvas
          edges={graph.edges}
          height="100vh"
          nodes={graph.nodes}
          onNodeLayoutChange={handleNodeLayoutChange}
          onNodeSelect={handleNodeSelect}
          onRenderedNodesChange={handleRenderedNodesChange}
          onStudioNodeRender={handleStudioNodeRender}
          onlyRenderVisibleElements={policy.visibleElementsOnly}
          selectedNodeId={selectedNodeId}
          showMiniMap={policy.minimap}
          variant="studio"
        />
      </React.Profiler>
    </main>
  );
}

export default function WorkflowCanvasBenchmarkPage() {
  let configuration: ReturnType<typeof readPageConfiguration>;
  try {
    configuration = readPageConfiguration();
  } catch (error) {
    return (
      <InvalidBenchmarkConfiguration
        message={error instanceof Error ? error.message : 'Invalid benchmark'}
      />
    );
  }

  return (
    <BenchmarkCanvas
      initialGraph={configuration.graph}
      policy={configuration.policy}
      size={configuration.size}
    />
  );
}
