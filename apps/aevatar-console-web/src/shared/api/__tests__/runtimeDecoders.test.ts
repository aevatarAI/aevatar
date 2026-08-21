import {
  decodeWorkflowCapabilitiesResponse,
  decodeWorkflowTemplateDetailResponse,
  decodeWorkflowTemplateListResponse,
} from '../runtimeDecoders';

const freshness = {
  projectionWatermark: '2026-08-18T00:00:00Z',
  lastEventId: 'event-template-12',
  versionSemantics: 'workflow-catalog-authority-state-version',
};

const template = {
  templateId: 'template-alpha',
  displayName: 'Incident triage',
  description: 'Classify an incident.',
  defaultDraftName: 'Incident triage',
  authorityStateVersion: 12,
  stepCount: 1,
  requiredConnections: ['pagerduty'],
  requiresLlmProvider: true,
  freshness,
};

describe('decodeWorkflowCapabilitiesResponse', () => {
  it('decodes primitive capabilities without the removed closedWorldBlocked field', () => {
    const decoded = decodeWorkflowCapabilitiesResponse({
      schemaVersion: '1',
      generatedAtUtc: '2026-05-24T00:00:00Z',
      primitives: [
        {
          name: 'llm_call',
          aliases: ['llm'],
          category: 'ai',
          description: 'Invoke an LLM provider.',
          runtimeModule: 'LlmCallModule',
          parameters: [
            {
              name: 'prompt',
              type: 'string',
              required: true,
              description: 'Prompt text.',
              default: '',
              enum: [],
            },
          ],
        },
      ],
      connectors: [],
      workflows: [],
    });

    expect(decoded.primitives).toHaveLength(1);
    expect(decoded.primitives[0].name).toBe('llm_call');
    expect(decoded.primitives[0]).not.toHaveProperty('closedWorldBlocked');
  });
});

describe('workflow template response decoders', () => {
  it('keeps template identity and authority version separate from draft identity', () => {
    const decoded = decodeWorkflowTemplateListResponse({
      items: [template],
      nextCursor: '12',
      freshness,
    });

    expect(decoded.items[0]).toMatchObject({
      templateId: 'template-alpha',
      authorityStateVersion: 12,
      requiredConnections: ['pagerduty'],
    });
    expect(decoded.items[0]).not.toHaveProperty('workflowId');
    expect(decoded.nextCursor).toBe('12');
  });

  it('decodes detail steps without inventing workflow or member identities', () => {
    const decoded = decodeWorkflowTemplateDetailResponse({
      template,
      yaml: 'name: incident_triage\nsteps: []\n',
      definition: {
        name: 'incident_triage',
        description: 'Classify an incident.',
        closedWorldMode: true,
        roles: [],
        steps: [
          {
            id: 'classify',
            type: 'llm_call',
            targetRole: 'responder',
            parameters: {},
            next: '',
            branches: {},
            children: [],
          },
        ],
      },
      edges: [],
      authorityStateVersion: 12,
      freshness,
    });

    expect(decoded.definition.steps[0]).toMatchObject({
      id: 'classify',
      type: 'llm_call',
      targetRole: 'responder',
    });
    expect(decoded.template.templateId).toBe('template-alpha');
    expect(decoded).not.toHaveProperty('memberId');
    expect(decoded).not.toHaveProperty('workflowId');
  });

  it('rejects missing authority versions instead of accepting stale creation data', () => {
    expect(() =>
      decodeWorkflowTemplateListResponse({
        items: [{ ...template, authorityStateVersion: undefined }],
        nextCursor: null,
        freshness,
      }),
    ).toThrow('authorityStateVersion must be a number');
  });
});
