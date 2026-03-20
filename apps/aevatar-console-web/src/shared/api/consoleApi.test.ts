import { consoleApi } from './consoleApi';

describe('consoleApi', () => {
  const originalFetch = global.fetch;

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
  });

  it('decodes agent summaries from the API boundary', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => [
        {
          id: 'agent-1',
          type: 'WorkflowAgent',
          description: 'Primary workflow agent',
        },
      ],
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(consoleApi.listAgents()).resolves.toEqual([
      {
        id: 'agent-1',
        type: 'WorkflowAgent',
        description: 'Primary workflow agent',
      },
    ]);
  });

  it('rejects malformed JSON payloads instead of trusting casts', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => [
        {
          id: 'agent-1',
          type: 'WorkflowAgent',
          description: 123,
        },
      ],
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(consoleApi.listAgents()).rejects.toThrow(
      'WorkflowAgentSummary[][0].description must be a string.',
    );
  });

  it('surfaces non-OK streamChat responses before SSE parsing starts', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: false,
      status: 400,
      text: async () => '{"message":"invalid workflow yaml"}',
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      consoleApi.streamChat(
        {
          prompt: 'Run it',
          workflowYamls: ['name: broken'],
        },
        new AbortController().signal,
      ),
    ).rejects.toThrow('invalid workflow yaml');
  });

  it('returns structured parse errors from the authoring parse endpoint', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: false,
      status: 400,
      text: async () =>
        JSON.stringify({
          valid: false,
          error: 'invalid yaml',
          errors: ['invalid yaml'],
          definition: null,
          edges: [],
        }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      consoleApi.parseWorkflow({
        yaml: 'broken',
      }),
    ).resolves.toEqual({
      valid: false,
      error: 'invalid yaml',
      errors: ['invalid yaml'],
      definition: null,
      edges: [],
    });
  });

  it('decodes the full runtime capability document', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        schemaVersion: 'capabilities.v1',
        generatedAtUtc: '2026-03-17T00:00:00Z',
        primitives: [
          {
            name: 'llm_call',
            aliases: ['llm'],
            category: 'ai',
            description: 'LLM invocation',
            closedWorldBlocked: false,
            runtimeModule: 'Aevatar.Workflow.Core.Primitives.LlmCall',
            parameters: [],
          },
        ],
        connectors: [
          {
            name: 'memory',
            type: 'mcp',
            enabled: true,
            timeoutMs: 15000,
            retry: 2,
            allowedInputKeys: ['query'],
            allowedOperations: ['search'],
            fixedArguments: [],
          },
        ],
        workflows: [
          {
            name: 'incident_triage',
            description: 'Triage workflow',
            source: 'repo',
            closedWorldMode: true,
            requiresLlmProvider: true,
            primitives: ['llm_call'],
            requiredConnectors: ['memory'],
            workflowCalls: [],
            steps: [
              {
                id: 'start',
                type: 'llm_call',
                next: '',
              },
            ],
          },
        ],
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(consoleApi.getCapabilities()).resolves.toEqual({
      schemaVersion: 'capabilities.v1',
      generatedAtUtc: '2026-03-17T00:00:00Z',
      primitives: [
        {
          name: 'llm_call',
          aliases: ['llm'],
          category: 'ai',
          description: 'LLM invocation',
          closedWorldBlocked: false,
          runtimeModule: 'Aevatar.Workflow.Core.Primitives.LlmCall',
          parameters: [],
        },
      ],
      connectors: [
        {
          name: 'memory',
          type: 'mcp',
          enabled: true,
          timeoutMs: 15000,
          retry: 2,
          allowedInputKeys: ['query'],
          allowedOperations: ['search'],
          fixedArguments: [],
        },
      ],
      workflows: [
        {
          name: 'incident_triage',
          description: 'Triage workflow',
          source: 'repo',
          closedWorldMode: true,
          requiresLlmProvider: true,
          primitives: ['llm_call'],
          requiredConnectors: ['memory'],
          workflowCalls: [],
          steps: [
            {
              id: 'start',
              type: 'llm_call',
              next: '',
            },
          ],
        },
      ],
    });
  });
});
