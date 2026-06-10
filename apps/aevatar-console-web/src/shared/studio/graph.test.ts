import { buildStudioGraphElements, formatStudioStepTypeLabel } from './graph';

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
});
