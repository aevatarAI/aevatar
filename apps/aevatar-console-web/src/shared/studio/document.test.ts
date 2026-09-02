import {
  applyRoleInspectorDraft,
  applyStepInspectorDraft,
  connectStepToTarget,
  createStepInspectorDraft,
  insertStepAfter,
  insertStepByType,
  materializeImplicitSequentialTransitions,
  parseInspectorBranches,
  parseInspectorParameters,
  removeStep,
  removeStepConnection,
  removeSteps,
  suggestBranchLabelForStep,
} from './document';
import type { StudioWorkflowDocument } from './models';

describe('studio document helpers', () => {
  it('round-trips an exact external operation capability independently from parameters', () => {
    const draft = createStepInspectorDraft({
      id: 'posthog_step',
      type: 'tool_call',
      targetRole: '',
      capability: {
        nyxid_operation: {
          user_service_id: 'us-posthog-alpha',
          endpoint_id: 'list-dashboards',
        },
      },
      parameters: {
        tool: 'nyxid_proxy',
        arguments: '{"query":{"limit":10}}',
      },
      next: null,
      branches: {},
    });

    expect(draft.capability).toEqual({
      nyxid_operation: {
        user_service_id: 'us-posthog-alpha',
        endpoint_id: 'list-dashboards',
      },
    });

    const result = applyStepInspectorDraft(
      {
        steps: [
          {
            id: 'posthog_step',
            type: 'tool_call',
            capability: draft.capability,
            parameters: {
              tool: 'nyxid_proxy',
              arguments: '{"query":{"limit":10}}',
            },
          },
        ],
      },
      'posthog_step',
      {
        ...draft,
        capability: {
          nyxid_operation: {
            user_service_id: 'us-posthog-beta',
            endpoint_id: 'update-dashboard',
          },
        },
      },
    );

    expect(result.document.steps?.[0]).toEqual(
      expect.objectContaining({
        capability: {
          nyxid_operation: {
            user_service_id: 'us-posthog-beta',
            endpoint_id: 'update-dashboard',
          },
        },
        parameters: {
          tool: 'nyxid_proxy',
          arguments: '{"query":{"limit":10}}',
        },
      }),
    );
  });

  it('removes an external capability without deleting runtime parameters', () => {
    const result = applyStepInspectorDraft(
      {
        steps: [
          {
            id: 'posthog_step',
            type: 'tool_call',
            capability: {
              nyxid_operation: {
                user_service_id: 'us-posthog-alpha',
                endpoint_id: 'list-dashboards',
              },
            },
            parameters: { tool: 'nyxid_proxy' },
          },
        ],
      },
      'posthog_step',
      {
        kind: 'step',
        id: 'posthog_step',
        type: 'tool_call',
        targetRole: '',
        next: '',
        branchesText: '{}',
        parametersText: '{"tool":"nyxid_proxy"}',
        capability: null,
      },
    );

    expect(result.document.steps?.[0]).not.toHaveProperty('capability');
    expect(result.document.steps?.[0]?.parameters).toEqual({
      tool: 'nyxid_proxy',
    });
  });

  it('materializes only eligible implicit sequential transitions without mutating the document', () => {
    const document: StudioWorkflowDocument = {
      name: 'workspace-demo',
      roles: [],
      steps: [
        {
          id: 'draft_step',
          type: 'llm_call',
          next: null,
          branches: {},
        },
        {
          id: 'review_step',
          type: 'human_approval',
          next: 'publish_step',
          branches: {},
        },
        {
          id: 'publish_step',
          type: 'emit',
          next: null,
          branches: {},
        },
      ],
    };
    const snapshot = structuredClone(document);

    const result = materializeImplicitSequentialTransitions(document);

    expect(result.steps?.map((step) => step.next)).toEqual([
      'review_step',
      'publish_step',
      null,
    ]);
    expect(document).toEqual(snapshot);
    expect(result).not.toBe(document);
  });

  it('preserves branched transitions and terminal documents', () => {
    const branched = materializeImplicitSequentialTransitions({
      name: 'branched',
      roles: [],
      steps: [
        {
          id: 'approval_step',
          type: 'human_approval',
          next: null,
          branches: { approved: 'publish_step' },
        },
        {
          id: 'publish_step',
          type: 'emit',
          next: null,
          branches: {},
        },
      ],
    });

    expect(branched.steps?.[0]).toEqual(
      expect.objectContaining({
        next: null,
        branches: { approved: 'publish_step' },
      }),
    );
    expect(
      materializeImplicitSequentialTransitions({ steps: [] }).steps,
    ).toEqual([]);
    expect(
      materializeImplicitSequentialTransitions({
        steps: [
          {
            id: 'only_step',
            type: 'llm_call',
            next: null,
            branches: {},
          },
        ],
      }).steps?.[0]?.next,
    ).toBeNull();

    const firstInsertion = insertStepByType(
      materializeImplicitSequentialTransitions({ steps: [] }),
      'assign',
    );
    expect(firstInsertion.document.steps).toEqual([
      expect.objectContaining({ id: 'assign_step', next: null }),
    ]);
  });

  it('creates inserted step ids from product names instead of backend step type ids', () => {
    const document: StudioWorkflowDocument = {
      name: 'workspace-demo',
      roles: [{ id: 'assistant' }],
      steps: [
        {
          id: 'llm_step',
          type: 'llm_call',
          targetRole: 'assistant',
          parameters: {},
          next: null,
          branches: {},
        },
      ],
    };

    const result = insertStepByType(document, 'llm_call', {
      targetRoleId: 'assistant',
    });

    expect(result.nodeId).toBe('step:llm_step_2');
    expect(result.document.steps?.[1]).toEqual(
      expect.objectContaining({
        id: 'llm_step_2',
        type: 'llm_call',
        originalType: 'llm_call',
        targetRole: 'assistant',
      }),
    );
  });

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
      capability: null,
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

  it('normalizes llm_call prompt parameters to prompt_prefix on apply', () => {
    const document: StudioWorkflowDocument = {
      name: 'workspace-demo',
      roles: [],
      steps: [
        {
          id: 'draft_step',
          type: 'llm_call',
          targetRole: 'assistant',
          parameters: {
            prompt: 'Legacy prompt field',
          },
          next: null,
          branches: {},
        },
      ],
    };

    const result = applyStepInspectorDraft(document, 'draft_step', {
      kind: 'step',
      id: 'draft_step',
      type: 'llm_call',
      targetRole: 'assistant',
      next: '',
      branchesText: '{}',
      parametersText: JSON.stringify({
        prompt: 'Translate the input to Japanese.',
      }),
      capability: null,
    });

    expect(result.document.steps?.[0]?.parameters).toEqual({
      prompt_prefix: 'Translate the input to Japanese.',
    });
  });

  it('keeps human prompt parameters unchanged on apply', () => {
    const document: StudioWorkflowDocument = {
      name: 'workspace-demo',
      roles: [],
      steps: [
        {
          id: 'approval_step',
          type: 'human_approval',
          targetRole: null,
          parameters: {
            prompt: 'Approve this?',
          },
          next: null,
          branches: {},
        },
      ],
    };

    const result = applyStepInspectorDraft(document, 'approval_step', {
      kind: 'step',
      id: 'approval_step',
      type: 'human_approval',
      targetRole: '',
      next: '',
      branchesText: '{}',
      parametersText: JSON.stringify({
        prompt: 'Approve the generated response?',
      }),
      capability: null,
    });

    expect(result.document.steps?.[0]?.parameters).toEqual({
      prompt: 'Approve the generated response?',
    });
  });

  it('rejects non-object raw node configuration', () => {
    expect(() => parseInspectorParameters('["nope"]')).toThrow(
      'Raw node configuration must be a JSON object.',
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

  it('removes a selected step and clears incident connections without rewiring flow', () => {
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
        next: null,
      }),
      expect.objectContaining({
        id: 'approve_step',
        branches: {},
      }),
    ]);
  });

  it('removes multiple selected steps without inventing replacement connections', () => {
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
          type: 'guard',
          targetRole: null,
          parameters: {},
          next: 'publish_step',
          branches: {},
        },
        {
          id: 'publish_step',
          type: 'emit',
          targetRole: null,
          parameters: {},
          next: null,
          branches: {
            retry: 'review_step',
          },
        },
      ],
    };

    const result = removeSteps(document, ['approve_step', 'review_step']);

    expect(result.nodeId).toBe('step:publish_step');
    expect(result.document.steps).toEqual([
      expect.objectContaining({
        id: 'draft_step',
        next: null,
      }),
      expect.objectContaining({
        id: 'publish_step',
        branches: {},
      }),
    ]);
  });

  it('removes an explicit next connection while keeping both steps', () => {
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
      ],
    };

    const result = removeStepConnection(
      document,
      'draft_step',
      'approve_step',
    );

    expect(result.nodeId).toBe('step:draft_step');
    expect(result.document.steps).toEqual([
      expect.objectContaining({
        id: 'draft_step',
        next: null,
      }),
      expect.objectContaining({
        id: 'approve_step',
      }),
    ]);
  });

  it('removes a branch connection while preserving sibling branches', () => {
    const document: StudioWorkflowDocument = {
      name: 'workspace-demo',
      roles: [],
      steps: [
        {
          id: 'guard_step',
          type: 'conditional',
          targetRole: null,
          parameters: {},
          next: null,
          branches: {
            true: 'approve_step',
            false: 'retry_step',
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

    const result = removeStepConnection(
      document,
      'guard_step',
      'retry_step',
      'false',
    );

    expect(result.nodeId).toBe('step:guard_step');
    expect(result.document.steps?.[0]).toEqual(
      expect.objectContaining({
        branches: {
          true: 'approve_step',
        },
      }),
    );
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
