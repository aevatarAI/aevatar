import {
  buildStudioGraphElements,
  formatStudioStepTypeLabel,
  needsStudioAutoLayout,
} from './graph';

describe('studio graph helpers', () => {
  it('formats step type labels without leaking backend identifiers', () => {
    expect(formatStudioStepTypeLabel('llm_call')).toBe('LLM call');
    expect(formatStudioStepTypeLabel('workflow_yaml_validate')).toBe(
      'Workflow YAML validation',
    );
    expect(formatStudioStepTypeLabel('')).toBe('Step');
  });

  it('keeps backend step type as the contract id and exposes a user-facing subtitle', () => {
    const graph = buildStudioGraphElements({
      name: 'workflow-demo',
      steps: [
        {
          id: 'triage',
          type: 'llm_call',
          parameters: {},
        },
      ],
    });

    expect(graph.nodes[0]?.data.stepType).toBe('llm_call');
    expect(graph.nodes[0]?.data.subtitle).toBe('LLM call');
  });

  it('summarizes llm prompt prefixes with user-facing wording', () => {
    const graph = buildStudioGraphElements({
      name: 'workflow-demo',
      steps: [
        {
          id: 'llm_call',
          type: 'llm_call',
          parameters: {
            prompt_prefix: 'Translate the user input to Japanese.',
          },
        },
      ],
    });

    expect(graph.nodes[0]?.data.parametersSummary).toBe(
      'instruction: Translate the user input to Japanese.',
    );
  });

  it('does not show empty llm prompt object defaults as configured prompts', () => {
    const graph = buildStudioGraphElements({
      name: 'workflow-demo',
      steps: [
        {
          id: 'llm_call',
          type: 'llm_call',
          parameters: {
            prompt_prefix: {},
          },
        },
      ],
    });

    expect(graph.nodes[0]?.data.parametersSummary).toBe(
      'No parameters configured',
    );
  });

  it('does not invent edges from adjacent disconnected steps', () => {
    const graph = buildStudioGraphElements({
      name: 'workflow-demo',
      steps: [
        {
          id: 'draft',
          type: 'transform',
          parameters: {},
        },
        {
          id: 'review',
          type: 'assign',
          parameters: {},
        },
      ],
    });

    expect(graph.nodes).toHaveLength(2);
    expect(graph.edges).toHaveLength(0);
  });

  it('renders explicit next and branch connections', () => {
    const graph = buildStudioGraphElements({
      name: 'workflow-demo',
      steps: [
        {
          id: 'draft',
          type: 'transform',
          next: 'review',
          parameters: {},
        },
        {
          id: 'review',
          type: 'conditional',
          branches: {
            true: 'publish',
          },
          parameters: {},
        },
        {
          id: 'publish',
          type: 'emit',
          parameters: {},
        },
      ],
    });

    expect(graph.edges.map((edge) => edge.id)).toEqual([
      'edge:draft:review:linear',
      'edge:review:publish:branch:true',
    ]);
  });

  it('keeps a branch labeled next distinct from a linear next connection', () => {
    const graph = buildStudioGraphElements({
      name: 'workflow-demo',
      steps: [
        {
          id: 'guard',
          type: 'conditional',
          next: 'linear_target',
          branches: {
            next: 'branch_target',
          },
          parameters: {},
        },
        {
          id: 'linear_target',
          type: 'emit',
          parameters: {},
        },
        {
          id: 'branch_target',
          type: 'emit',
          parameters: {},
        },
      ],
    });

    expect(graph.edges.map((edge) => edge.id)).toEqual([
      'edge:guard:linear_target:linear',
      'edge:guard:branch_target:branch:next',
    ]);
  });

  it('skips automatic layout when every step has a saved position', () => {
    const document = {
      name: 'workflow-demo',
      steps: [
        { id: 'draft', type: 'transform', parameters: {} },
        { id: 'publish', type: 'emit', parameters: {} },
      ],
    };
    const savedPositions = {
      draft: { x: -120.5, y: 34 },
      publish: { x: 981, y: -47.25 },
    };

    expect(needsStudioAutoLayout(document.steps, savedPositions)).toBe(false);
    expect(
      buildStudioGraphElements(document, {
        nodePositions: savedPositions,
      }).nodes.map((node) => node.position),
    ).toEqual([savedPositions.draft, savedPositions.publish]);
  });

  it('keeps valid partial positions while deterministically laying out missing steps', () => {
    const document = {
      name: 'workflow-demo',
      steps: [
        { id: 'draft', type: 'transform', parameters: {} },
        { id: 'review', type: 'conditional', parameters: {} },
        { id: 'publish', type: 'emit', parameters: {} },
      ],
    };
    const savedPositions = {
      draft: { x: 77, y: -53 },
    };

    expect(needsStudioAutoLayout(document.steps, savedPositions)).toBe(true);

    const firstGraph = buildStudioGraphElements(document, {
      nodePositions: savedPositions,
    });
    const secondGraph = buildStudioGraphElements(document, {
      nodePositions: savedPositions,
    });
    const positions = firstGraph.nodes.map((node) => node.position);

    expect(positions[0]).toEqual(savedPositions.draft);
    expect(positions).toEqual(secondGraph.nodes.map((node) => node.position));
    expect(positions.slice(1)).toEqual([
      { x: 240, y: 540 },
      { x: 240, y: 900 },
    ]);
    expect(
      positions.every(({ x, y }) => Number.isFinite(x) && Number.isFinite(y)),
    ).toBe(true);
    expect(new Set(positions.map(({ x, y }) => `${x}:${y}`)).size).toBe(
      positions.length,
    );
  });

  it('ignores unknown and invalid saved positions before deciding on automatic layout', () => {
    const document = {
      name: 'workflow-demo',
      steps: [
        { id: 'draft', type: 'transform', parameters: {} },
        { id: 'publish', type: 'emit', parameters: {} },
      ],
    };
    const graph = buildStudioGraphElements(document, {
      nodePositions: {
        draft: { x: 14, y: 28 },
        publish: { x: Number.NaN, y: 40 },
        unknown: { x: 200, y: 300 },
      },
    });

    expect(
      needsStudioAutoLayout(document.steps, { draft: { x: 14, y: 28 } }),
    ).toBe(true);
    expect(graph.nodes[0]?.position).toEqual({ x: 14, y: 28 });
    expect(graph.nodes[1]?.position).toEqual({ x: 240, y: 540 });
  });
});
