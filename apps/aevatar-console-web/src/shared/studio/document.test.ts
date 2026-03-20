import {
  applyRoleInspectorDraft,
  applyStepInspectorDraft,
  connectStepToTarget,
  insertStepAfter,
  parseInspectorBranches,
  parseInspectorParameters,
  removeStep,
  suggestBranchLabelForStep,
} from './document';
import type { StudioWorkflowDocument } from './models';

describe('studio document helpers', () => {
  it('updates role fields and rewrites step role bindings', () => {
    const document: StudioWorkflowDocument = {
      name: 'workspace-demo',
      roles: [
        {
          id: 'assistant',
          name: 'Assistant',
          provider: 'tornado',
          model: 'gpt-test',
          systemPrompt: 'Help the operator.',
          connectors: ['web-search'],
        },
      ],
      steps: [
        {
          id: 'draft_step',
          type: 'llm_call',
          targetRole: 'assistant',
          parameters: {},
          next: null,
          branches: {},
        },
      ],
    };

    const result = applyRoleInspectorDraft(document, 'assistant', {
      kind: 'role',
      id: 'reviewer',
      name: 'Reviewer',
      provider: 'openai',
      model: 'gpt-4.1',
      systemPrompt: 'Review the output.',
      connectorsText: 'memory\nweb-search',
    });

    expect(result.nodeId).toBe('role:reviewer');
    expect(result.document.roles).toEqual([
      expect.objectContaining({
        id: 'reviewer',
        name: 'Reviewer',
        provider: 'openai',
        model: 'gpt-4.1',
        systemPrompt: 'Review the output.',
        connectors: ['memory', 'web-search'],
      }),
    ]);
    expect(result.document.steps).toEqual([
      expect.objectContaining({
        targetRole: 'reviewer',
      }),
    ]);
  });

  it('updates step fields and rewrites next and branch references', () => {
    const document: StudioWorkflowDocument = {
      name: 'workspace-demo',
      roles: [],
      steps: [
        {
          id: 'draft_step',
          type: 'llm_call',
          targetRole: 'assistant',
          parameters: {},
          next: 'approve_step',
          branches: {},
        },
        {
          id: 'approve_step',
          type: 'human_approval',
          targetRole: null,
          parameters: {},
          next: null,
          branches: {
            approved: 'draft_step',
          },
        },
      ],
    };

    const result = applyStepInspectorDraft(document, 'draft_step', {
      kind: 'step',
      id: 'review_step',
      type: 'connector_call',
      targetRole: 'reviewer',
      next: 'approve_step',
      branchesText: '{"approved":"approve_step"}',
      parametersText: '{"connector":"web-search","limit":3}',
    });

    expect(result.nodeId).toBe('step:review_step');
    expect(result.document.steps).toEqual([
      expect.objectContaining({
        id: 'review_step',
        type: 'connector_call',
        originalType: 'connector_call',
        targetRole: 'reviewer',
        next: 'approve_step',
        branches: {
          approved: 'approve_step',
        },
        parameters: {
          connector: 'web-search',
          limit: 3,
        },
      }),
      expect.objectContaining({
        branches: {
          approved: 'review_step',
        },
      }),
    ]);
  });

  it('rejects non-object step parameters', () => {
    expect(() => parseInspectorParameters('["nope"]')).toThrow(
      'Step parameters must be a JSON object.',
    );
  });

  it('inserts a new step after the selected step and rewires the linear next edge', () => {
    const document: StudioWorkflowDocument = {
      name: 'workspace-demo',
      roles: [{ id: 'assistant' }],
      steps: [
        {
          id: 'draft_step',
          type: 'llm_call',
          targetRole: 'assistant',
          parameters: {},
          next: 'approve_step',
          branches: {},
        },
        {
          id: 'approve_step',
          type: 'human_approval',
          targetRole: null,
          parameters: {},
          next: null,
          branches: {},
        },
      ],
    };

    const result = insertStepAfter(document, 'draft_step');

    expect(result.nodeId).toBe('step:draft_step_next');
    expect(result.document.steps).toEqual([
      expect.objectContaining({
        id: 'draft_step',
        next: 'draft_step_next',
      }),
      expect.objectContaining({
        id: 'draft_step_next',
        type: 'llm_call',
        targetRole: 'assistant',
        next: 'approve_step',
      }),
      expect.objectContaining({
        id: 'approve_step',
      }),
    ]);
  });

  it('removes a selected step and rewrites inbound edges to the next step', () => {
    const document: StudioWorkflowDocument = {
      name: 'workspace-demo',
      roles: [],
      steps: [
        {
          id: 'draft_step',
          type: 'llm_call',
          targetRole: 'assistant',
          parameters: {},
          next: 'review_step',
          branches: {},
        },
        {
          id: 'review_step',
          type: 'connector_call',
          targetRole: 'assistant',
          parameters: {},
          next: 'approve_step',
          branches: {},
        },
        {
          id: 'approve_step',
          type: 'human_approval',
          targetRole: null,
          parameters: {},
          next: null,
          branches: {
            retry: 'review_step',
          },
        },
      ],
    };

    const result = removeStep(document, 'review_step');

    expect(result.nodeId).toBe('step:approve_step');
    expect(result.document.steps).toEqual([
      expect.objectContaining({
        id: 'draft_step',
        next: 'approve_step',
      }),
      expect.objectContaining({
        id: 'approve_step',
        branches: {
          retry: 'approve_step',
        },
      }),
    ]);
  });

  it('connects a step to a new linear next target', () => {
    const document: StudioWorkflowDocument = {
      name: 'workspace-demo',
      roles: [],
      steps: [
        {
          id: 'draft_step',
          type: 'llm_call',
          targetRole: 'assistant',
          parameters: {},
          next: 'approve_step',
          branches: {},
        },
        {
          id: 'approve_step',
          type: 'human_approval',
          targetRole: null,
          parameters: {},
          next: null,
          branches: {},
        },
        {
          id: 'publish_step',
          type: 'emit',
          targetRole: null,
          parameters: {},
          next: null,
          branches: {},
        },
      ],
    };

    const result = connectStepToTarget(
      document,
      'draft_step',
      'publish_step',
    );

    expect(result.nodeId).toBe('step:draft_step');
    expect(result.document.steps?.[0]).toEqual(
      expect.objectContaining({
        id: 'draft_step',
        next: 'publish_step',
        branches: {},
      }),
    );
  });

  it('connects a conditional step through a branch label', () => {
    const document: StudioWorkflowDocument = {
      name: 'workspace-demo',
      roles: [],
      steps: [
        {
          id: 'guard_step',
          type: 'conditional',
          targetRole: 'assistant',
          parameters: {},
          next: null,
          branches: {
            true: 'approve_step',
          },
        },
        {
          id: 'approve_step',
          type: 'human_approval',
          targetRole: null,
          parameters: {},
          next: null,
          branches: {},
        },
        {
          id: 'retry_step',
          type: 'llm_call',
          targetRole: 'assistant',
          parameters: {},
          next: null,
          branches: {},
        },
      ],
    };

    const result = connectStepToTarget(
      document,
      'guard_step',
      'retry_step',
      'false',
    );

    expect(result.document.steps?.[0]).toEqual(
      expect.objectContaining({
        branches: {
          true: 'approve_step',
          false: 'retry_step',
        },
      }),
    );
  });

  it('suggests branch labels that match the app editor defaults', () => {
    expect(suggestBranchLabelForStep('conditional', {})).toBe('true');
    expect(
      suggestBranchLabelForStep('conditional', { true: 'next_step' }),
    ).toBe('false');
    expect(suggestBranchLabelForStep('switch', {})).toBe('_default');
    expect(suggestBranchLabelForStep('llm_call', {})).toBeNull();
  });

  it('rejects non-object step branches', () => {
    expect(() => parseInspectorBranches('["nope"]')).toThrow(
      'Step branches must be a JSON object.',
    );
  });
});
