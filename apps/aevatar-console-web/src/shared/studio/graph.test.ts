import {
  buildStudioGraphElements,
  formatStudioStepTypeLabel,
  needsStudioAutoLayout,
  type StudioGraphStep,
} from './graph';

function createStudioGraphStep(
  id: string,
  overrides: Partial<Omit<StudioGraphStep, 'id'>> = {},
): StudioGraphStep {
  return {
    id,
    type: 'transform',
    targetRole: '',
    parameters: {},
    next: null,
    branches: {},
    ...overrides,
  };
}

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
    const steps = [
      createStudioGraphStep('draft'),
      createStudioGraphStep('publish', { type: 'emit' }),
    ];
    const document = {
      name: 'workflow-demo',
      steps,
    };
    const savedPositions = {
      draft: { x: -120.5, y: 34 },
      publish: { x: 981, y: -47.25 },
    };

    expect(needsStudioAutoLayout(steps, savedPositions)).toBe(false);
    expect(
      buildStudioGraphElements(document, {
        nodePositions: savedPositions,
      }).nodes.map((node) => node.position),
    ).toEqual([savedPositions.draft, savedPositions.publish]);
  });

  it('keeps valid partial positions while deterministically laying out missing steps', () => {
    const steps = [
      createStudioGraphStep('draft'),
      createStudioGraphStep('review', { type: 'conditional' }),
      createStudioGraphStep('publish', { type: 'emit' }),
    ];
    const document = {
      name: 'workflow-demo',
      steps,
    };
    const savedPositions = {
      draft: { x: 77, y: -53 },
    };

    expect(needsStudioAutoLayout(steps, savedPositions)).toBe(true);

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
    const steps = [
      createStudioGraphStep('draft'),
      createStudioGraphStep('publish', { type: 'emit' }),
    ];
    const document = {
      name: 'workflow-demo',
      steps,
    };
    const graph = buildStudioGraphElements(document, {
      nodePositions: {
        draft: { x: 14, y: 28 },
        publish: { x: Number.NaN, y: 40 },
        unknown: { x: 200, y: 300 },
      },
    });

    expect(needsStudioAutoLayout(steps, { draft: { x: 14, y: 28 } })).toBe(
      true,
    );
    expect(graph.nodes[0]?.position).toEqual({ x: 14, y: 28 });
    expect(graph.nodes[1]?.position).toEqual({ x: 240, y: 540 });
  });

  it('moves missing nodes away from an exactly colliding saved position', () => {
    const savedPositions = {
      draft: { x: 240, y: 540 },
    };
    const document = {
      name: 'workflow-demo',
      steps: [
        createStudioGraphStep('draft'),
        createStudioGraphStep('review', { type: 'conditional' }),
      ],
    };

    const firstGraph = buildStudioGraphElements(document, {
      nodePositions: savedPositions,
    });
    const secondGraph = buildStudioGraphElements(document, {
      nodePositions: savedPositions,
    });

    expect(savedPositions).toEqual({ draft: { x: 240, y: 540 } });
    expect(firstGraph.nodes.map((node) => node.position)).toEqual([
      { x: 240, y: 540 },
      { x: 240, y: 740 },
    ]);
    expect(firstGraph.nodes.map((node) => node.position)).toEqual(
      secondGraph.nodes.map((node) => node.position),
    );
  });

  it('moves missing nodes away from saved node footprints, not only matching coordinates', () => {
    const document = {
      name: 'workflow-demo',
      steps: [
        createStudioGraphStep('draft'),
        createStudioGraphStep('review', { type: 'conditional' }),
      ],
    };

    const graph = buildStudioGraphElements(document, {
      nodePositions: {
        draft: { x: 240, y: 600 },
      },
    });

    expect(graph.nodes.map((node) => node.position)).toEqual([
      { x: 240, y: 600 },
      { x: 240, y: 740 },
    ]);
  });

  it('does not treat inherited saved-position keys as complete layout positions', () => {
    const steps = [createStudioGraphStep('toString')];

    expect(needsStudioAutoLayout(steps, {})).toBe(true);
  });

  it('lays out inherited-key step ids without colliding with saved positions', () => {
    const savedPositions = {
      draft: { x: 570, y: 180 },
    };
    const document = {
      name: 'workflow-demo',
      steps: [
        createStudioGraphStep('draft'),
        createStudioGraphStep('toString', { type: 'conditional' }),
      ],
    };

    const firstGraph = buildStudioGraphElements(document, {
      nodePositions: savedPositions,
    });
    const secondGraph = buildStudioGraphElements(document, {
      nodePositions: savedPositions,
    });
    const positions = firstGraph.nodes.map((node) => node.position);

    expect(savedPositions).toEqual({ draft: { x: 570, y: 180 } });
    expect(positions).toEqual([
      { x: 570, y: 180 },
      { x: 240, y: 540 },
    ]);
    expect(positions).toEqual(secondGraph.nodes.map((node) => node.position));
    expect(
      positions.every(({ x, y }) => Number.isFinite(x) && Number.isFinite(y)),
    ).toBe(true);
  });
});
