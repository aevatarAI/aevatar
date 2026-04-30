import { studioApi } from './api';
import { persistAuthSession } from '@/shared/auth/session';

describe('studioApi host-session requests', () => {
  const originalFetch = global.fetch;
  const originalEnv = { ...process.env };

  beforeEach(() => {
    window.localStorage.clear();
    process.env = {
      ...originalEnv,
    };
    delete process.env.ORNN_BASE_URL;
    jest.spyOn(Date, 'now').mockReturnValue(1_700_000_000_000);
  });

  afterEach(() => {
    global.fetch = originalFetch;
    process.env = originalEnv;
    jest.restoreAllMocks();
    window.localStorage.clear();
  });

  it('injects the NyxID bearer token for Studio host endpoints', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        enabled: true,
        authenticated: true,
        providerDisplayName: 'NyxID',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await studioApi.getAuthSession();

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/auth/me');
    expect(init?.credentials).toBe('same-origin');
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
  });

  it('loads template workflows from the Studio host using bearer auth', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        catalog: {
          name: 'published-demo',
          description: 'Published demo workflow',
          category: '',
          group: '',
          groupLabel: '',
          sortOrder: 0,
          source: 'catalog',
          sourceLabel: 'Published templates',
          showInLibrary: true,
          isPrimitiveExample: false,
          requiresLlmProvider: false,
          primitives: [],
        },
        yaml: 'name: published-demo\nsteps: []\n',
        definition: {
          name: 'published-demo',
          description: 'Published demo workflow',
          closedWorldMode: false,
          roles: [],
          steps: [],
        },
        edges: [],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await studioApi.getTemplateWorkflow('published-demo');

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/workflows/published-demo');
    expect(init?.credentials).toBe('same-origin');
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
  });

  it('loads user config from the Studio host using bearer auth', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        defaultModel: 'gpt-5.4-mini',
        preferredLlmRoute: '',
        runtimeMode: 'local',
        localRuntimeBaseUrl: 'https://aevatar-console-backend-api.aevatar.ai',
        remoteRuntimeBaseUrl: 'https://aevatar-console-backend-api.aevatar.ai',
        maxToolRounds: 40,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await studioApi.getUserConfig();

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/user-config');
    expect(init?.credentials).toBe('same-origin');
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
  });

  it('decodes NyxID model metadata from snake_case response fields', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        providers: [
          {
            provider_slug: 'openai',
            provider_name: 'OpenAI',
            source: 'user_service',
            status: 'ready',
            proxy_url: 'https://nyx.example/proxy/openai',
          },
        ],
        gateway_url: 'https://nyx.example/gateway',
        models_by_provider: {
          openai: ['gpt-5.4-mini'],
        },
        supported_models: ['gpt-5.4-mini'],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(studioApi.getUserConfigModels()).resolves.toEqual({
      providers: [
        {
          providerSlug: 'openai',
          providerName: 'OpenAI',
          source: 'user_service',
          status: 'ready',
          proxyUrl: 'https://nyx.example/proxy/openai',
        },
      ],
      gatewayUrl: 'https://nyx.example/gateway',
      modelsByProvider: {
        openai: ['gpt-5.4-mini'],
      },
      supportedModels: ['gpt-5.4-mini'],
    });
  });

  it('loads Ornn skills from the Ornn platform using bearer auth', async () => {
    process.env.ORNN_BASE_URL = 'https://ornn.example.com';
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        baseUrl: 'https://ornn.chrono-ai.fun',
        total: 0,
        totalPages: 0,
        page: 1,
        pageSize: 100,
        items: [],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await studioApi.searchSkills({ query: 'ornn', pageSize: 100 });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe(
      'https://ornn.example.com/api/web/skill-search?query=ornn&mode=keyword&scope=mixed&page=1&pageSize=100',
    );
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
  });

  it('returns a stable empty skill result when ORNN_BASE_URL is invalid', async () => {
    process.env.ORNN_BASE_URL = '://bad-url';
    const fetchMock = jest.fn();
    global.fetch = fetchMock as typeof global.fetch;

    await expect(studioApi.searchSkills()).resolves.toEqual({
      baseUrl: '',
      total: 0,
      totalPages: 0,
      page: 1,
      pageSize: 50,
      items: [],
      message:
        'ORNN_BASE_URL must be a valid http(s) URL or a root-relative path such as /ornn.',
    });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('surfaces RFC 9110 problem details as a readable Studio error', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: false,
      status: 404,
      text: async () =>
        JSON.stringify({
          type: 'https://tools.ietf.org/html/rfc9110#section-15.5.5',
          title: 'Not Found',
          status: 404,
          traceId: '00-trace',
        }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(studioApi.getWorkflow('missing-workflow')).rejects.toThrow(
      'Not Found',
    );
  });

  it('includes the requested scope when loading a scoped workflow draft', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        workflowId: 'workflow-1',
        name: 'scope-demo',
        fileName: 'scope-demo.yaml',
        filePath: 'scope://scope-1/workflow-1.yaml',
        directoryId: 'scope:scope-1',
        directoryLabel: 'scope-1',
        yaml: 'name: scope-demo\nsteps: []\n',
        layout: null,
        updatedAtUtc: '2026-04-16T00:00:00Z',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await studioApi.getWorkflow('workflow-1', 'scope-1');

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/workspace/workflow-drafts/workflow-1?scopeId=scope-1');
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
  });

  it('merges scoped published workflows with draft workflows when listing workflows', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockImplementation(async (input: string) => {
      if (input === '/api/workspace/workflow-drafts?scopeId=scope-1') {
        return {
          ok: true,
          status: 200,
          json: async () => [
            {
              workflowId: 'workflow-draft',
              name: 'draft-demo',
              description: 'draft copy',
              fileName: 'draft-demo.yaml',
              filePath: 'scope://scope-1/workflow-draft.yaml',
              directoryId: 'scope:scope-1',
              directoryLabel: 'scope-1',
              stepCount: 1,
              hasLayout: true,
              updatedAtUtc: '2026-04-16T00:00:00Z',
            },
          ],
        } as Response;
      }

      if (input === '/api/scopes/scope-1/workflows?includeSource=false') {
        return {
          ok: true,
          status: 200,
          json: async () => [
            {
              scopeId: 'scope-1',
              workflowId: 'workflow-draft',
              displayName: 'published draft demo',
              serviceKey: 'svc-draft',
              workflowName: 'draft-demo',
              actorId: 'actor-draft',
              activeRevisionId: 'rev-draft',
              deploymentId: 'dep-draft',
              deploymentStatus: 'Running',
              updatedAt: '2026-04-15T00:00:00Z',
            },
            {
              scopeId: 'scope-1',
              workflowId: 'workflow-published',
              displayName: 'published-demo',
              serviceKey: 'svc-published',
              workflowName: 'published-demo',
              actorId: 'actor-published',
              activeRevisionId: 'rev-published',
              deploymentId: 'dep-published',
              deploymentStatus: 'Running',
              updatedAt: '2026-04-14T00:00:00Z',
            },
          ],
        } as Response;
      }

      throw new Error(`Unexpected request: ${input}`);
    });
    global.fetch = fetchMock as typeof global.fetch;

    await expect(studioApi.listWorkflows('scope-1')).resolves.toEqual([
      {
        workflowId: 'workflow-draft',
        name: 'draft-demo',
        description: 'draft copy',
        fileName: 'draft-demo.yaml',
        filePath: 'scope://scope-1/workflow-draft.yaml',
        directoryId: 'scope:scope-1',
        directoryLabel: 'scope-1',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-04-16T00:00:00Z',
      },
      {
        workflowId: 'workflow-published',
        name: 'published-demo',
        description: '',
        fileName: 'workflow-published.yaml',
        filePath: 'scope://scope-1/workflow-published.yaml',
        directoryId: 'scope:scope-1',
        directoryLabel: 'scope-1',
        stepCount: 0,
        hasLayout: false,
        updatedAtUtc: '2026-04-14T00:00:00Z',
      },
    ]);
  });

  it('falls back to the published scope workflow detail when a scoped draft is missing', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockImplementation(async (input: string) => {
      if (input === '/api/workspace/workflow-drafts/workflow-1?scopeId=scope-1') {
        return {
          ok: false,
          status: 404,
          statusText: 'Not Found',
          text: async () => JSON.stringify({ title: 'Not Found', status: 404 }),
        } as Response;
      }

      if (input === '/api/scopes/scope-1/workflows/workflow-1') {
        return {
          ok: true,
          status: 200,
          json: async () => ({
            available: true,
            scopeId: 'scope-1',
            workflow: {
              scopeId: 'scope-1',
              workflowId: 'workflow-1',
              displayName: 'published-demo',
              serviceKey: 'svc-1',
              workflowName: 'published-demo',
              actorId: 'actor-1',
              activeRevisionId: 'rev-1',
              deploymentId: 'dep-1',
              deploymentStatus: 'Running',
              updatedAt: '2026-04-16T00:00:00Z',
            },
            source: {
              workflowYaml: 'name: published-demo\nsteps: []\n',
              definitionActorId: 'definition-1',
              inlineWorkflowYamls: null,
            },
          }),
        } as Response;
      }

      throw new Error(`Unexpected request: ${input}`);
    });
    global.fetch = fetchMock as typeof global.fetch;

    await expect(studioApi.getWorkflow('workflow-1', 'scope-1')).resolves.toEqual({
      workflowId: 'workflow-1',
      name: 'published-demo',
      fileName: 'workflow-1.yaml',
      filePath: 'scope://scope-1/workflow-1.yaml',
      directoryId: 'scope:scope-1',
      directoryLabel: 'scope-1',
      yaml: 'name: published-demo\nsteps: []\n',
      document: null,
      draftExists: false,
      findings: [],
      updatedAtUtc: '2026-04-16T00:00:00Z',
    });
  });

  it('creates a scoped workflow draft on first save when the loaded workflow is committed-only', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        workflowId: 'workflow-1',
        name: 'scope-demo',
        fileName: 'scope-demo.yaml',
        filePath: 'scope://scope-1/workflow-1.yaml',
        directoryId: 'scope:scope-1',
        directoryLabel: 'scope-1',
        yaml: 'name: scope-demo\nsteps: []\n',
        layout: null,
        updatedAtUtc: '2026-04-16T00:00:00Z',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await studioApi.saveWorkflow({
      workflowId: 'workflow-1',
      draftExists: false,
      scopeId: 'scope-1',
      directoryId: 'scope:scope-1',
      workflowName: 'scope-demo',
      yaml: 'name: scope-demo\nsteps: []\n',
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/workspace/workflow-drafts?scopeId=scope-1');
    expect(init?.method).toBe('POST');
  });

  it('includes the requested scope when updating a scoped workflow draft', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        workflowId: 'workflow-1',
        name: 'scope-demo',
        fileName: 'scope-demo.yaml',
        filePath: 'scope://scope-1/workflow-1.yaml',
        directoryId: 'scope:scope-1',
        directoryLabel: 'scope-1',
        yaml: 'name: scope-demo\nsteps: []\n',
        layout: null,
        updatedAtUtc: '2026-04-16T00:00:00Z',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await studioApi.saveWorkflow({
      workflowId: 'workflow-1',
      scopeId: 'scope-1',
      directoryId: 'scope:scope-1',
      workflowName: 'scope-demo',
      yaml: 'name: scope-demo\nsteps: []\n',
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/workspace/workflow-drafts/workflow-1?scopeId=scope-1');
    expect(init?.method).toBe('PUT');
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
  });

  it('includes the requested scope when deleting a scoped workflow draft', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 204,
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await studioApi.deleteWorkflow('workflow-1', 'scope-1');

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/workspace/workflow-drafts/workflow-1?scopeId=scope-1');
    expect(init?.method).toBe('DELETE');
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
  });

  it('collapses HTML error pages into a compact HTTP error message', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: false,
      status: 502,
      statusText: 'Bad Gateway',
      text: async () => `<!DOCTYPE html>
<html lang="en-US">
  <head>
    <title>aevatar.ai | 502: Bad gateway</title>
  </head>
  <body>
    <h1>Bad gateway</h1>
  </body>
</html>`,
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(studioApi.getAuthSession()).rejects.toThrow(
      'HTTP 502 Bad Gateway',
    );
  });

  it('sends available step types when parsing workflow yaml', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        document: {
          name: 'demo_template',
          description: '',
          roles: [],
          steps: [],
        },
        graph: null,
        findings: [],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await studioApi.parseYaml({
      yaml: 'name: demo_template\nsteps:\n  - id: step_1\n    type: demo_template\n',
      availableWorkflowNames: ['workspace-demo'],
      availableStepTypes: ['demo_template', 'llm_call'],
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/editor/parse-yaml');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({
      yaml: 'name: demo_template\nsteps:\n  - id: step_1\n    type: demo_template\n',
      availableWorkflowNames: ['workspace-demo'],
      availableStepTypes: ['demo_template', 'llm_call'],
    });
  });

  it('binds a saved script to the default service using the scope binding endpoint', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        scopeId: 'scope-1',
        serviceId: 'default',
        displayName: 'script-1',
        revisionId: 'rev-1',
        implementationKind: 2,
        expectedActorId: 'definition-scope-1',
        script: {
          scriptId: 'script-1',
          scriptRevision: 'rev-1',
          definitionActorId: 'definition',
        },
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    const result = await studioApi.bindScopeScript({
      scopeId: 'scope-1',
      displayName: 'script-1',
      serviceId: 'script-1',
      scriptId: 'script-1',
      scriptRevision: 'rev-1',
    });

    expect(result.implementationKind).toBe('script');
    expect(result.targetKind).toBe('script');
    expect(result.targetName).toBe('script-1');
    expect(result.script).toEqual({
      scriptId: 'script-1',
      scriptRevision: 'rev-1',
      definitionActorId: 'definition',
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/scopes/scope-1/binding');
    expect(init?.method).toBe('PUT');
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
    expect(JSON.parse(String(init?.body))).toEqual({
      implementationKind: 'script',
      displayName: 'script-1',
      serviceId: 'script-1',
      script: {
        scriptId: 'script-1',
        scriptRevision: 'rev-1',
      },
    });
  });

  it('binds a workflow to a member-owned published service using the member binding endpoint', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        scopeId: 'scope-1',
        publishedServiceId: 'joker',
        displayName: 'joker',
        revisionId: 'rev-1',
        implementationKind: 'workflow',
        workflow: {
          workflowName: 'joker',
          definitionActorIdPrefix: 'scope-workflow:scope-1:joker',
        },
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    const result = await studioApi.bindMemberWorkflow({
      scopeId: 'scope-1',
      memberId: 'joker',
      displayName: 'joker',
      workflowYamls: ['name: joker\nsteps: []\n'],
      revisionId: 'rev-1',
    });

    expect(result.serviceId).toBe('joker');
    expect(result.implementationKind).toBe('workflow');
    expect(result.targetKind).toBe('workflow');
    expect(result.workflow).toEqual({
      workflowName: 'joker',
      definitionActorIdPrefix: 'scope-workflow:scope-1:joker',
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/scopes/scope-1/members/joker/binding');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(String(init?.body))).toEqual({
      implementationKind: 'workflow',
      displayName: 'joker',
      workflow: {
        workflowYamls: ['name: joker\nsteps: []\n'],
      },
      revisionId: 'rev-1',
    });
  });

  it('binds a GAgent to the default service using the scope binding endpoint', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        scopeId: 'scope-1',
        serviceId: 'default',
        displayName: 'orders-gagent',
        revisionId: 'rev-1',
        implementationKind: 'GAgent',
        expectedActorId: 'orders-gagent:dep-1',
        gAgent: {
          actorTypeName: 'Tests.OrdersGAgent, Tests',
        },
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    const result = await studioApi.bindScopeGAgent({
      scopeId: 'scope-1',
      displayName: 'orders-gagent',
      actorTypeName: 'Tests.OrdersGAgent, Tests',
      revisionId: 'rev-1',
      endpoints: [
        {
          endpointId: 'run',
          displayName: 'Run',
          kind: 'command',
          requestTypeUrl: 'type.googleapis.com/google.protobuf.StringValue',
          description: 'Run the bound gagent.',
        },
      ],
    });

    expect(result.implementationKind).toBe('gagent');
    expect(result.targetKind).toBe('gagent');
    expect(result.targetName).toBe('Tests.OrdersGAgent, Tests');
    expect(result.gAgent).toEqual({
      actorTypeName: 'Tests.OrdersGAgent, Tests',
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/scopes/scope-1/binding');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(String(init?.body))).toEqual({
      implementationKind: 'gagent',
      displayName: 'orders-gagent',
      gagent: {
        actorTypeName: 'Tests.OrdersGAgent, Tests',
        endpoints: [
          {
            endpointId: 'run',
            displayName: 'Run',
            kind: 'command',
            requestTypeUrl: 'type.googleapis.com/google.protobuf.StringValue',
            description: 'Run the bound gagent.',
          },
        ],
      },
      revisionId: 'rev-1',
    });
  });

  it('normalizes scope binding revisions from backend implementation names', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        available: true,
        scopeId: 'scope-1',
        serviceId: 'default',
        displayName: 'Script Service',
        serviceKey: 'scope-1/default',
        defaultServingRevisionId: 'rev-2',
        activeServingRevisionId: 'rev-2',
        deploymentId: 'deploy-2',
        deploymentStatus: 'Active',
        primaryActorId: 'actor://scope/default',
        updatedAt: '2026-03-26T08:00:00Z',
        revisions: [
          {
            revisionId: 'rev-2',
            implementationKind: 'Scripting',
            status: 'Published',
            artifactHash: 'hash-2',
            failureReason: '',
            isDefaultServing: true,
            isActiveServing: true,
            isServingTarget: true,
            allocationWeight: 100,
            servingState: 'Active',
            deploymentId: 'deploy-2',
            primaryActorId: 'actor://scope/default',
            createdAt: '2026-03-26T07:00:00Z',
            preparedAt: '2026-03-26T07:01:00Z',
            publishedAt: '2026-03-26T07:02:00Z',
            retiredAt: null,
            scriptId: 'script-alpha',
            scriptRevision: 'script-rev-1',
            scriptDefinitionActorId: 'definition://script-alpha',
            scriptSourceHash: 'hash-1',
          },
        ],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    const status = await studioApi.getScopeBinding('scope-1');

    expect(status.revisions[0]?.implementationKind).toBe('script');
    expect(status.revisions[0]?.scriptId).toBe('script-alpha');
  });

  it('loads member binding status from member-first response fields', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        available: true,
        scopeId: 'scope-1',
        publishedServiceId: 'joker',
        displayName: 'joker',
        publishedServiceKey: 'scope-1:default:joker',
        defaultServingRevisionId: 'rev-2',
        activeServingRevisionId: 'rev-2',
        deploymentId: 'deploy-2',
        deploymentStatus: 'Active',
        primaryActorId: 'actor://scope/joker',
        updatedAt: '2026-03-26T08:00:00Z',
        revisions: [],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(studioApi.getMemberBinding('scope-1', 'joker')).resolves.toEqual(
      expect.objectContaining({
        serviceId: 'joker',
        serviceKey: 'scope-1:default:joker',
        displayName: 'joker',
      }),
    );

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/members/joker/binding',
      expect.objectContaining({
        credentials: 'same-origin',
      }),
    );
  });

  it('lists studio members from the member roster endpoint', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        scopeId: 'scope-1',
        members: [
          {
            memberId: 'joker',
            scopeId: 'scope-1',
            displayName: 'joker',
            description: 'Support workflow member',
            implementationKind: 'workflow',
            lifecycleStage: 'bind_ready',
            publishedServiceId: 'member-joker',
            lastBoundRevisionId: 'rev-2',
            createdAt: '2026-04-27T08:00:00Z',
            updatedAt: '2026-04-27T08:05:00Z',
          },
        ],
        nextPageToken: null,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(studioApi.listMembers('scope-1')).resolves.toEqual({
      scopeId: 'scope-1',
      members: [
        {
          memberId: 'joker',
          scopeId: 'scope-1',
          teamId: null,
          displayName: 'joker',
          description: 'Support workflow member',
          implementationKind: 'workflow',
          lifecycleStage: 'bind_ready',
          publishedServiceId: 'member-joker',
          lastBoundRevisionId: 'rev-2',
          createdAt: '2026-04-27T08:00:00Z',
          updatedAt: '2026-04-27T08:05:00Z',
        },
      ],
      nextPageToken: null,
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/members',
      expect.objectContaining({
        credentials: 'same-origin',
      }),
    );
  });

  it('gets a studio member detail from the member authority endpoint', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        summary: {
          memberId: 'joker',
          scopeId: 'scope-1',
          displayName: 'joker',
          description: 'Support workflow member',
          implementationKind: 'workflow',
          lifecycleStage: 'bind_ready',
          publishedServiceId: 'member-joker',
          lastBoundRevisionId: 'rev-2',
          createdAt: '2026-04-27T08:00:00Z',
          updatedAt: '2026-04-27T08:05:00Z',
        },
        implementationRef: {
          implementationKind: 'workflow',
          workflowId: 'joker',
          workflowRevision: 'rev-2',
        },
        lastBinding: {
          publishedServiceId: 'member-joker',
          revisionId: 'rev-2',
          implementationKind: 'workflow',
          boundAt: '2026-04-27T08:05:00Z',
        },
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(studioApi.getMember('scope-1', 'joker')).resolves.toEqual({
      summary: {
        memberId: 'joker',
        scopeId: 'scope-1',
        teamId: null,
        displayName: 'joker',
        description: 'Support workflow member',
        implementationKind: 'workflow',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'member-joker',
        lastBoundRevisionId: 'rev-2',
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
      implementationRef: {
        implementationKind: 'workflow',
        workflowId: 'joker',
        workflowRevision: 'rev-2',
        scriptId: null,
        scriptRevision: null,
        actorTypeName: null,
      },
      lastBinding: {
        publishedServiceId: 'member-joker',
        revisionId: 'rev-2',
        implementationKind: 'workflow',
        boundAt: '2026-04-27T08:05:00Z',
      },
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/members/joker',
      expect.objectContaining({
        credentials: 'same-origin',
      }),
    );
  });

  it('creates a workflow member through the member-first create endpoint', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 201,
      json: async () => ({
        memberId: 'orders-draft',
        scopeId: 'scope-1',
        teamId: 'team-orders',
        displayName: 'orders-draft',
        description: '',
        implementationKind: 'workflow',
        lifecycleStage: 'created',
        publishedServiceId: 'member-orders-draft',
        lastBoundRevisionId: null,
        createdAt: '2026-04-27T08:10:00Z',
        updatedAt: '2026-04-27T08:10:00Z',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.createMember({
        scopeId: 'scope-1',
        displayName: 'orders-draft',
        implementationKind: 'workflow',
        teamId: 'team-orders',
      }),
    ).resolves.toEqual({
      memberId: 'orders-draft',
      scopeId: 'scope-1',
      teamId: 'team-orders',
      displayName: 'orders-draft',
      description: '',
      implementationKind: 'workflow',
      lifecycleStage: 'created',
      publishedServiceId: 'member-orders-draft',
      lastBoundRevisionId: null,
      createdAt: '2026-04-27T08:10:00Z',
      updatedAt: '2026-04-27T08:10:00Z',
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/members',
      expect.objectContaining({
        credentials: 'same-origin',
        method: 'POST',
        body: JSON.stringify({
          displayName: 'orders-draft',
          implementationKind: 'workflow',
          teamId: 'team-orders',
        }),
      }),
    );
  });

  it('lists teams through the team-first studio endpoint', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        scopeId: 'scope-1',
        teams: [
          {
            teamId: 'team-support',
            scopeId: 'scope-1',
            displayName: 'Support Team',
            description: 'Handles inbound support requests',
            lifecycleStage: 'active',
            memberCount: 2,
            createdAt: '2026-04-27T08:00:00Z',
            updatedAt: '2026-04-27T08:05:00Z',
          },
        ],
        nextPageToken: null,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(studioApi.listTeams('scope-1')).resolves.toEqual({
      scopeId: 'scope-1',
      teams: [
        {
          teamId: 'team-support',
          scopeId: 'scope-1',
          displayName: 'Support Team',
          description: 'Handles inbound support requests',
          lifecycleStage: 'active',
          memberCount: 2,
          createdAt: '2026-04-27T08:00:00Z',
          updatedAt: '2026-04-27T08:05:00Z',
        },
      ],
      nextPageToken: null,
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/teams',
      expect.objectContaining({
        credentials: 'same-origin',
      }),
    );
  });

  it('creates a team through the team-first studio endpoint', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 201,
      json: async () => ({
        teamId: 'team-support',
        scopeId: 'scope-1',
        displayName: 'Support Team',
        description: 'Handles inbound support requests',
        lifecycleStage: 'active',
        memberCount: 0,
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:00:00Z',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.createTeam({
        scopeId: 'scope-1',
        displayName: 'Support Team',
        description: 'Handles inbound support requests',
      }),
    ).resolves.toEqual({
      teamId: 'team-support',
      scopeId: 'scope-1',
      displayName: 'Support Team',
      description: 'Handles inbound support requests',
      lifecycleStage: 'active',
      memberCount: 0,
      createdAt: '2026-04-27T08:00:00Z',
      updatedAt: '2026-04-27T08:00:00Z',
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/teams',
      expect.objectContaining({
        credentials: 'same-origin',
        method: 'POST',
        body: JSON.stringify({
          displayName: 'Support Team',
          description: 'Handles inbound support requests',
        }),
      }),
    );
  });

  it('updates a member team assignment through the patch endpoint', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        summary: {
          memberId: 'joker',
          scopeId: 'scope-1',
          teamId: 'team-support',
          displayName: 'joker',
          description: 'Support workflow member',
          implementationKind: 'workflow',
          lifecycleStage: 'bind_ready',
          publishedServiceId: 'member-joker',
          lastBoundRevisionId: 'rev-2',
          createdAt: '2026-04-27T08:00:00Z',
          updatedAt: '2026-04-27T08:05:00Z',
        },
        implementationRef: null,
        lastBinding: null,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.updateMemberTeam('scope-1', 'joker', 'team-support'),
    ).resolves.toEqual({
      summary: {
        memberId: 'joker',
        scopeId: 'scope-1',
        teamId: 'team-support',
        displayName: 'joker',
        description: 'Support workflow member',
        implementationKind: 'workflow',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'member-joker',
        lastBoundRevisionId: 'rev-2',
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
      implementationRef: null,
      lastBinding: null,
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/members/joker',
      expect.objectContaining({
        credentials: 'same-origin',
        method: 'PATCH',
        body: JSON.stringify({
          teamId: 'team-support',
        }),
      }),
    );
  });

  it('retires a scope binding revision through the studio binding API', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        scopeId: 'scope-1',
        serviceId: 'default',
        revisionId: 'rev-2',
        status: 'Retiring',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.retireScopeBindingRevision({
        scopeId: 'scope-1',
        revisionId: 'rev-2',
      }),
    ).resolves.toEqual({
      scopeId: 'scope-1',
      serviceId: 'default',
      revisionId: 'rev-2',
      status: 'Retiring',
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/binding/revisions/rev-2:retire',
      expect.objectContaining({
        method: 'POST',
      }),
    );
  });
});
