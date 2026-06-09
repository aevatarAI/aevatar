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
});
