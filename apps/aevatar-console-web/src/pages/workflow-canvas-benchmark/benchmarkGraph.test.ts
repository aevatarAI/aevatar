import {
  addWorkflowCanvasBenchmarkTopology,
  assertWorkflowCanvasBenchmarkResult,
  countChangedNodeReferences,
  createWorkflowCanvasBenchmarkGraph,
  createWorkflowCanvasBenchmarkProgress,
  parseWorkflowCanvasBenchmarkGraphSize,
  WORKFLOW_CANVAS_BENCHMARK_GRAPH_SIZES,
  type WorkflowCanvasBenchmarkResult,
} from './benchmarkGraph';

describe('workflow canvas benchmark graph', () => {
  it.each(
    WORKFLOW_CANVAS_BENCHMARK_GRAPH_SIZES,
  )('creates a deterministic, realistic %i-node graph with two forward targets where available', (size) => {
    const first = createWorkflowCanvasBenchmarkGraph(size);
    const second = createWorkflowCanvasBenchmarkGraph(size);

    expect(first).toEqual(second);
    expect(first.nodes).toHaveLength(size);
    expect(first.edges).toHaveLength(size * 2 - 3);
    expect(new Set(first.nodes.map((node) => node.id)).size).toBe(size);
    expect(new Set(first.edges.map((edge) => edge.id)).size).toBe(size * 2 - 3);

    first.nodes.forEach((node, index) => {
      expect(node.initialWidth).toBe(268);
      expect(node.initialHeight).toBe(120);
      expect(Number.isFinite(node.position.x)).toBe(true);
      expect(Number.isFinite(node.position.y)).toBe(true);
      expect(Object.keys(node.data).sort()).toEqual([
        'branchCount',
        'executionFocused',
        'executionStatus',
        'kind',
        'label',
        'parametersSummary',
        'stepId',
        'stepType',
        'subtitle',
        'targetRole',
        'title',
      ]);
      expect(node.data).toEqual(
        expect.objectContaining({
          branchCount: index + 2 < size ? 1 : 0,
          executionFocused: false,
          executionStatus: 'idle',
          kind: 'step',
          label: expect.any(String),
          parametersSummary: expect.any(String),
          stepId: node.id,
          stepType: expect.any(String),
          subtitle: expect.any(String),
          targetRole: expect.any(String),
          title: expect.any(String),
        }),
      );

      const targets = first.edges
        .filter((edge) => edge.source === node.id)
        .map((edge) => edge.target);
      expect(targets).toEqual(
        [first.nodes[index + 1]?.id, first.nodes[index + 2]?.id].filter(
          (target): target is string => Boolean(target),
        ),
      );
    });
  });

  it('accepts only the three supported graph sizes', () => {
    expect(parseWorkflowCanvasBenchmarkGraphSize('100')).toBe(100);
    expect(parseWorkflowCanvasBenchmarkGraphSize('500')).toBe(500);
    expect(parseWorkflowCanvasBenchmarkGraphSize('1000')).toBe(1000);

    for (const value of [null, '', '0', '99', '101', '500.0', '10000']) {
      expect(() => parseWorkflowCanvasBenchmarkGraphSize(value)).toThrow(
        'Unsupported workflow canvas benchmark graph size',
      );
    }
  });

  it('adds realistic topology while preserving every unaffected node reference', () => {
    const graph = createWorkflowCanvasBenchmarkGraph(100);
    const expanded = addWorkflowCanvasBenchmarkTopology(graph);

    expect(expanded.nodes).toHaveLength(101);
    expect(expanded.edges).toHaveLength(graph.edges.length + 2);
    expect(expanded.nodes.slice(0, -3)).toEqual(graph.nodes.slice(0, -2));
    graph.nodes.slice(0, -2).forEach((node, index) => {
      expect(expanded.nodes[index]).toBe(node);
    });
    expect(expanded.nodes.at(-3)).not.toBe(graph.nodes.at(-2));
    expect(expanded.nodes.at(-2)).toBe(graph.nodes.at(-1));
    expect(expanded.nodes.at(-1)?.data).toEqual(
      expect.objectContaining({
        branchCount: 0,
        executionStatus: 'idle',
        kind: 'step',
      }),
    );
    expect(countChangedNodeReferences(graph.nodes, expanded.nodes)).toBe(2);
  });
});

describe('workflow canvas benchmark artifact progress', () => {
  it('formats complete and partial result counts for the Markdown artifact', () => {
    expect(createWorkflowCanvasBenchmarkProgress(96, 96)).toEqual({
      complete: true,
      markdownLines: ['- Complete: yes', '- Results: 96/96'],
    });
    expect(createWorkflowCanvasBenchmarkProgress(17, 96)).toEqual({
      complete: false,
      markdownLines: ['- Complete: no', '- Results: 17/96'],
    });
  });
});

describe('workflow canvas benchmark result schema', () => {
  const validResult: WorkflowCanvasBenchmarkResult = {
    browser: 'Chrome/140.0.0.0',
    buildMode: 'production',
    changedNodeReferences: 1,
    durationMs: 12.5,
    graph: { edges: 197, nodes: 100 },
    longTasks: 0,
    policy: { minimap: true, visibleElementsOnly: true },
    renderedNodeCount: 24,
    scenario: 'status-update',
    usedHeapBytes: null,
  };

  it('returns valid results unchanged', () => {
    expect(assertWorkflowCanvasBenchmarkResult(validResult)).toBe(validResult);
    expect(
      assertWorkflowCanvasBenchmarkResult({
        ...validResult,
        usedHeapBytes: 42_000_000,
      }),
    ).toEqual({ ...validResult, usedHeapBytes: 42_000_000 });
  });

  it('rejects malformed, unsupported, non-finite, and extended result values', () => {
    const invalidResults: unknown[] = [
      null,
      { ...validResult, buildMode: 'development' },
      { ...validResult, browser: '' },
      { ...validResult, graph: { ...validResult.graph, nodes: 101 } },
      { ...validResult, graph: { ...validResult.graph, edges: -1 } },
      { ...validResult, policy: { ...validResult.policy, minimap: 'yes' } },
      { ...validResult, scenario: 'restore-viewport' },
      { ...validResult, durationMs: Number.NaN },
      { ...validResult, longTasks: 0.5 },
      { ...validResult, renderedNodeCount: -1 },
      { ...validResult, changedNodeReferences: Number.POSITIVE_INFINITY },
      { ...validResult, usedHeapBytes: -1 },
      { ...validResult, unexpected: true },
    ];

    invalidResults.forEach((result) => {
      expect(() => assertWorkflowCanvasBenchmarkResult(result)).toThrow(
        'Invalid workflow canvas benchmark result',
      );
    });
  });
});
