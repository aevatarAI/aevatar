import { persistAuthSession } from '@/shared/auth/session';
import { StudioApiError, studioApi } from './api';
import type { StudioExplicitRequestConfirmation } from './models';

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

  it('decodes canonical LLM settings from the Studio host', async () => {
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
        savedSelection: {
          routeKind: 'nyx_id_user_service',
          routeValue: '/api/v1/proxy/s/chrono-llm-public',
          nyxIdUserServiceId: 'us-alpha',
          serviceSlugSnapshot: 'chrono-llm-public',
          modelSelection: {
            kind: 'explicit_model',
            modelId: 'gpt-5.5',
          },
        },
        savedRouteLabel: 'OpenAI beta',
        selectionStatus: 'needs_repair',
        catalogDiagnostic: 'route_not_ready',
        remediation: 'choose_replacement',
        catalogStatus: 'ready',
        capabilities: {
          canEditRoute: true,
          canEditModel: true,
          canSave: true,
          canRetryCatalog: false,
        },
        routeOptions: [
          {
            routeValue: '/api/v1/llm/gateway/v1',
            label: 'Company LLM Gateway',
            source: 'gateway_provider',
            status: 'ready',
            allowed: true,
            ready: true,
            userServiceId: null,
            serviceSlug: null,
            modelCatalog: {
              certainty: 'not_verifiable',
              modelIds: [],
              defaultModelId: null,
              diagnostic: 'not_published',
            },
            description: null,
          },
          {
            routeValue: '/api/v1/proxy/s/openai',
            label: 'OpenAI',
            source: 'user_service',
            status: 'ready',
            allowed: true,
            ready: true,
            userServiceId: 'us-alpha',
            serviceSlug: 'openai',
            modelCatalog: {
              certainty: 'enumerated',
              modelIds: ['gpt-5.5'],
              defaultModelId: 'gpt-5.5',
              diagnostic: 'unspecified',
            },
            description: null,
          },
        ],
        modelGroupsByRoute: [
          {
            routeValue: '',
            groupId: 'openai',
            label: 'OpenAI',
            models: ['gpt-5.4-mini'],
          },
        ],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    const settings = await studioApi.getUserLlmSettings();
    expect(settings).toMatchObject({
      savedSelection: {
        routeKind: 'nyx_id_user_service',
        routeValue: '/api/v1/proxy/s/chrono-llm-public',
        nyxIdUserServiceId: 'us-alpha',
        modelSelection: { kind: 'explicit_model', modelId: 'gpt-5.5' },
      },
      selectionStatus: 'needs_repair',
      remediation: 'choose_replacement',
    });
    expect(settings).not.toHaveProperty('effectiveRoute');
    expect(settings).not.toHaveProperty('routeFallbackActive');
  });

  it('decodes an omitted model selection for the system default route', async () => {
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
        savedSelection: {
          routeKind: 'unspecified',
          routeValue: '',
          nyxIdUserServiceId: '',
          serviceSlugSnapshot: '',
        },
        savedRouteLabel: 'System default',
        selectionStatus: 'system_default',
        catalogDiagnostic: 'unspecified',
        remediation: 'none',
        catalogStatus: 'ready',
        capabilities: {
          canEditRoute: true,
          canEditModel: true,
          canSave: true,
          canRetryCatalog: false,
        },
        routeOptions: [],
        modelGroupsByRoute: [],
        setupHint: null,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(studioApi.getUserLlmSettings()).resolves.toMatchObject({
      savedSelection: {
        routeKind: 'unspecified',
        modelSelection: { kind: 'unspecified' },
      },
      selectionStatus: 'system_default',
    });
  });

  it('rejects an unknown LLM selection enum at the adapter boundary', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        savedSelection: {
          routeKind: 'future_selection_kind',
          routeValue: '/api/v1/proxy/s/openai',
          nyxIdUserServiceId: 'us-alpha',
          serviceSlugSnapshot: 'openai',
          modelSelection: { kind: 'provider_default', modelId: null },
        },
        savedRouteLabel: 'OpenAI alpha',
        selectionStatus: 'needs_repair',
        catalogDiagnostic: 'route_not_ready',
        remediation: 'choose_replacement',
        routeOptions: [],
        modelGroupsByRoute: [],
        catalogStatus: 'ready',
        capabilities: {
          canEditRoute: true,
          canEditModel: true,
          canSave: true,
          canRetryCatalog: false,
        },
        setupHint: null,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(studioApi.getUserLlmSettings()).rejects.toThrow(
      'StudioUserLlmSettings.savedSelection.routeKind is not supported.',
    );
  });

  it.each([
    ['reset', { action: 'reset' } as const],
    [
      'Gateway',
      {
        action: 'select_gateway',
        gateway: { model: { kind: 'provider_default' } },
      } as const,
    ],
    [
      'user service',
      {
        action: 'select_user_service',
        userService: {
          userServiceId: 'us-beta',
          model: { kind: 'explicit_model', modelId: 'gpt-5.4-mini' },
        },
      } as const,
    ],
    [
      'preset',
      {
        action: 'activate_preset',
        preset: { presetId: 'work-fast' },
      } as const,
    ],
  ])('sends the typed %s LLM intent unchanged', async (_label, intent) => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: { sub: 'user-1' },
    });
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 202,
      json: async () => ({
        accepted: true,
        commandId: 'cmd-typed',
        ackStage: 'accepted_for_dispatch',
        actorId: 'user-1',
        correlationId: 'corr-1',
        ackedAtUtc: '2026-07-23T08:00:00Z',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await studioApi.saveUserLlmSettings(intent);

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/user-config/llm',
      expect.objectContaining({
        method: 'PUT',
        body: JSON.stringify(intent),
      }),
    );
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

    await expect(
      studioApi.getWorkflow('missing-workflow'),
    ).rejects.toMatchObject({
      message: 'Not Found',
      status: 404,
    });
    await expect(
      studioApi.getWorkflow('missing-workflow'),
    ).rejects.toBeInstanceOf(StudioApiError);
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
    expect(input).toBe(
      '/api/workspace/workflow-drafts/workflow-1?scopeId=scope-1',
    );
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
              publishedServiceId: 'published-draft',
              serviceAppId: 'workflow-app',
              serviceNamespace: 'workflow-namespace',
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
              publishedServiceId: 'published-workflow',
              serviceAppId: 'workflow-app',
              serviceNamespace: 'workflow-namespace',
            },
          ],
        } as Response;
      }

      throw new Error(`Unexpected request: ${input}`);
    });
    global.fetch = fetchMock as typeof global.fetch;

    await expect(studioApi.listWorkflows('scope-1')).resolves.toEqual([
      {
        activeRevisionId: 'rev-draft',
        serviceKey: 'svc-draft',
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
        activeRevisionId: 'rev-published',
        serviceKey: 'svc-published',
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

  it('loads committed source from the scope list when a scoped draft is missing', async () => {
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
      if (
        input === '/api/workspace/workflow-drafts/workflow-1?scopeId=scope-1'
      ) {
        return {
          ok: false,
          status: 404,
          statusText: 'Not Found',
          text: async () => JSON.stringify({ title: 'Not Found', status: 404 }),
        } as Response;
      }

      if (input === '/api/scopes/scope-1/workflows?includeSource=true') {
        return {
          ok: true,
          status: 200,
          json: async () => [
            {
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
                deploymentStatus: 'Pending',
                updatedAt: '2026-04-16T00:00:00Z',
                publishedServiceId: 'published-workflow-1',
                serviceAppId: 'workflow-app',
                serviceNamespace: 'workflow-namespace',
              },
              source: {
                workflowYaml: 'name: published-demo\nsteps: []\n',
                definitionActorId: 'definition-1',
                inlineWorkflowYamls: null,
              },
            },
          ],
        } as Response;
      }

      throw new Error(`Unexpected request: ${input}`);
    });
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.getWorkflow('workflow-1', 'scope-1'),
    ).resolves.toEqual({
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
    expect(fetchMock.mock.calls.map(([input]) => input)).toEqual([
      '/api/workspace/workflow-drafts/workflow-1?scopeId=scope-1',
      '/api/scopes/scope-1/workflows?includeSource=true',
    ]);
  });

  it('loads a published scope workflow detail without checking workspace drafts', async () => {
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
        workflow: {
          scopeId: 'scope-1',
          workflowId: 'workflow-1',
          displayName: 'published-demo-v2',
          serviceKey: 'svc-1',
          workflowName: 'published-demo-v2',
          actorId: 'actor-1',
          activeRevisionId: 'rev-2',
          deploymentId: 'dep-1',
          deploymentStatus: 'Running',
          updatedAt: '2026-04-17T00:00:00Z',
          publishedServiceId: 'published-workflow-1',
          serviceAppId: 'workflow-app',
          serviceNamespace: 'workflow-namespace',
        },
        source: {
          workflowYaml: 'name: published-demo-v2\nsteps: []\n',
          definitionActorId: 'definition-1',
          inlineWorkflowYamls: null,
        },
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.getPublishedWorkflow('workflow-1', 'scope-1'),
    ).resolves.toEqual({
      workflowId: 'workflow-1',
      name: 'published-demo-v2',
      fileName: 'workflow-1.yaml',
      filePath: 'scope://scope-1/workflow-1.yaml',
      directoryId: 'scope:scope-1',
      directoryLabel: 'scope-1',
      yaml: 'name: published-demo-v2\nsteps: []\n',
      document: null,
      draftExists: false,
      findings: [],
      updatedAtUtc: '2026-04-17T00:00:00Z',
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      '/api/scopes/scope-1/workflows/workflow-1',
    );
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

  it('instantiates a public template with its authority version and decodes the accepted draft receipt', async () => {
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
      status: 202,
      json: async () => ({
        accepted: true,
        workflowId: 'wf-template-draft',
        commandId: 'cmd-template-instantiate',
        ackStage: 'accepted',
        actorId: 'actor-workspace',
        workspaceId: 'workspace-scope-alpha',
        expectedVersion: 1,
        ackedAtUtc: '2026-08-18T00:00:00Z',
        readiness: {
          readable: false,
          stage: 'materializing',
          message: 'Draft accepted.',
        },
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.instantiateWorkflowTemplate({
        expectedAuthorityStateVersion: 12,
        scopeId: 'scope-alpha',
        templateId: 'template-alpha',
      }),
    ).resolves.toMatchObject({
      workflowId: 'wf-template-draft',
      commandId: 'cmd-template-instantiate',
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe(
      '/api/scopes/scope-alpha/workflow-templates/template-alpha:instantiate',
    );
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({
      expectedAuthorityStateVersion: 12,
    });
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
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
    expect(input).toBe(
      '/api/workspace/workflow-drafts/workflow-1?scopeId=scope-1',
    );
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
    expect(input).toBe(
      '/api/workspace/workflow-drafts/workflow-1?scopeId=scope-1',
    );
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
      status: 202,
      json: async () => ({
        status: 'accepted',
        bindingRunId: 'bind-1',
        scopeId: 'scope-1',
        memberId: 'joker',
        ackStage: 'dispatch_accepted',
        bindingRunRole: 'candidate',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    const runtimeWorkflowYaml = 'name: joker-runtime\nsteps: []\n';
    const result = await studioApi.bindMemberWorkflow({
      scopeId: 'scope-1',
      memberId: 'joker',
      displayName: 'joker',
      workflowId: 'workflow-stable-1',
      workflowYamls: [runtimeWorkflowYaml],
      revisionId: 'rev-1',
    });

    expect(result).toEqual({
      status: 'accepted',
      bindingRunId: 'bind-1',
      scopeId: 'scope-1',
      memberId: 'joker',
      ackStage: 'dispatch_accepted',
      bindingRunRole: 'candidate',
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
        workflowId: 'workflow-stable-1',
        workflowYamls: [runtimeWorkflowYaml],
      },
      revisionId: 'rev-1',
    });
  });

  it('previews sanitized explicit requests and forwards only their confirmations to workflow publication transports', async () => {
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

    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({
          workflowId: 'wf-alpha',
          revisionId: 'rev-alpha',
          items: [
            {
              callSiteId: 'wf-alpha/request-alpha',
              requestContractDigest: 'digest-alpha',
              userServiceId: 'usvc-alpha',
              method: 'post',
              pathTemplate: '/records/{id}',
              bodyMode: 'json',
              bodyRequired: true,
              responseMode: 'text',
              effectiveRisk: 'write',
              approvalRequired: true,
              allowedExecutionModes: ['interactive'],
            },
          ],
        }),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 202,
        json: async () => ({
          status: 'accepted',
          bindingRunId: 'bind-alpha',
          scopeId: 'scope-alpha',
          memberId: 'm-alpha',
          ackStage: 'dispatch_accepted',
          bindingRunRole: 'candidate',
        }),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 202,
        json: async () => ({
          scopeId: 'scope-alpha',
          workflowId: 'wf-alpha',
          revisionId: 'rev-alpha',
          acceptanceStage: 'accepted',
          propagationStage: 'readmodel_propagating',
        }),
      } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    const preview = await studioApi.previewExplicitRequests({
      scopeId: 'scope-alpha',
      workflowId: 'wf-alpha',
      workflowYaml: 'name: Workflow Alpha\nsteps: []\n',
      executionMode: 'interactive',
      revisionId: 'rev-alpha',
    });

    expect(preview).toEqual({
      workflowId: 'wf-alpha',
      revisionId: 'rev-alpha',
      items: [
        {
          callSiteId: 'wf-alpha/request-alpha',
          requestContractDigest: 'digest-alpha',
          userServiceId: 'usvc-alpha',
          method: 'post',
          pathTemplate: '/records/{id}',
          bodyMode: 'json',
          bodyRequired: true,
          responseMode: 'text',
          effectiveRisk: 'write',
          approvalRequired: true,
          allowedExecutionModes: ['interactive'],
        },
      ],
    });

    const confirmations: StudioExplicitRequestConfirmation[] = [
      {
        workflowId: 'wf-alpha',
        revisionId: 'rev-alpha',
        callSiteId: 'wf-alpha/request-alpha',
        requestContractDigest: 'digest-alpha',
        attestedRisk: 'write',
      },
    ];
    await studioApi.bindMemberWorkflow({
      scopeId: 'scope-alpha',
      memberId: 'm-alpha',
      workflowId: 'wf-alpha',
      revisionId: 'rev-alpha',
      workflowYamls: ['name: Workflow Alpha\nsteps: []\n'],
      explicitRequestConfirmations: confirmations,
    });
    await studioApi.saveAndBindWorkflow({
      scopeId: 'scope-alpha',
      workflowId: 'wf-alpha',
      revisionId: 'rev-alpha',
      workflowYaml: 'name: Workflow Alpha\nsteps: []\n',
      explicitRequestConfirmations: confirmations,
    });

    expect(JSON.parse(String(fetchMock.mock.calls[0]?.[1]?.body))).toEqual({
      workflowYaml: 'name: Workflow Alpha\nsteps: []\n',
      executionMode: 'interactive',
      workflowId: 'wf-alpha',
      revisionId: 'rev-alpha',
    });
    expect(
      JSON.parse(String(fetchMock.mock.calls[1]?.[1]?.body)),
    ).toMatchObject({
      implementationKind: 'workflow',
      explicitRequestConfirmations: confirmations,
      workflow: {
        workflowId: 'wf-alpha',
      },
      revisionId: 'rev-alpha',
    });
    expect(JSON.parse(String(fetchMock.mock.calls[2]?.[1]?.body))).toEqual({
      workflowId: 'wf-alpha',
      revisionId: 'rev-alpha',
      workflowYaml: 'name: Workflow Alpha\nsteps: []\n',
      explicitRequestConfirmations: confirmations,
    });
  });

  it.each([
    ['callSiteId', { callSiteId: ' ' }],
    ['requestContractDigest', { requestContractDigest: ' ' }],
    ['userServiceId', { userServiceId: ' ' }],
    ['pathTemplate', { pathTemplate: ' ' }],
    ['duplicate callSiteId', { callSiteId: 'wf-alpha/request-alpha' }],
  ])('rejects malformed explicit-request preview item: %s', async (_label, override) => {
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

    const previewItem = {
      callSiteId: 'wf-alpha/request-alpha',
      requestContractDigest: 'digest-alpha',
      userServiceId: 'usvc-alpha',
      method: 'post',
      pathTemplate: '/records/{id}',
      bodyMode: 'json',
      bodyRequired: true,
      responseMode: 'text',
      effectiveRisk: 'write',
      approvalRequired: true,
      allowedExecutionModes: ['interactive'],
      ...override,
    };
    const previewItems =
      _label === 'duplicate callSiteId'
        ? [previewItem, { ...previewItem }]
        : [previewItem];
    global.fetch = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        workflowId: 'wf-alpha',
        revisionId: 'rev-alpha',
        items: previewItems,
      }),
    } as Response) as typeof global.fetch;

    await expect(
      studioApi.previewExplicitRequests({
        scopeId: 'scope-alpha',
        workflowId: 'wf-alpha',
        revisionId: 'rev-alpha',
        workflowYaml: 'name: Workflow Alpha\nsteps: []\n',
        executionMode: 'interactive',
      }),
    ).rejects.toThrow();
  });

  it('lists exact workflow capability selectors for the active scope', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        capabilities: [
          {
            displayName: 'PostHog / List dashboards',
            readOnly: true,
            destructive: false,
            selector: {
              kind: 'nyxid_operation',
              userServiceId: 'us-posthog-alpha',
              endpointId: 'list-dashboards',
            },
            source: {
              kind: 'nyxid_user_services',
              sourceId: 'source-posthog-alpha',
              sourceVersion: 7,
              observedAt: '2026-09-02T08:00:00+00:00',
              freshUntil: '2026-09-02T08:05:00+00:00',
            },
          },
        ],
        candidateCount: 2,
        rejectedCount: 1,
        diagnostics: [
          {
            code: 'unsupported_schema',
            safeMessage:
              'One operation was omitted because its schema is unsupported.',
            count: 1,
            source: null,
          },
        ],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.listWorkflowCapabilities(' scope-alpha '),
    ).resolves.toEqual({
      capabilities: [
        expect.objectContaining({
          displayName: 'PostHog / List dashboards',
          selector: {
            kind: 'nyxid_operation',
            userServiceId: 'us-posthog-alpha',
            endpointId: 'list-dashboards',
          },
        }),
      ],
      candidateCount: 2,
      rejectedCount: 1,
      diagnostics: [
        expect.objectContaining({
          code: 'unsupported_schema',
          count: 1,
        }),
      ],
    });
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-alpha/workflow-capabilities',
      expect.objectContaining({ credentials: 'same-origin' }),
    );
  });

  it('inspects a workflow capability readiness contract using only its exact selector', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        executionMode: 'interactive',
        status: 'ready',
        selectedSelector: {
          kind: 'nyxid_operation',
          userServiceId: 'us-posthog-alpha',
          endpointId: 'update-dashboard',
        },
        selectedOperation: {
          userServiceId: 'us-posthog-alpha',
          endpointId: 'update-dashboard',
          serviceSlug: 'posthog',
          httpMethod: 'PATCH',
          pathTemplate: '/api/dashboards/{dashboard_id}',
          parameters: [
            {
              name: 'dashboard_id',
              location: 'path',
              required: true,
              schema: {
                valueKind: 'string',
                properties: [],
                requiredProperties: [],
                items: null,
                allowedValues: [],
                additionalPropertiesAllowed: false,
              },
            },
          ],
          requestBody: {
            required: true,
            mediaType: 'application/json',
            schema: {
              valueKind: 'object',
              properties: [
                {
                  name: 'name',
                  schema: {
                    valueKind: 'string',
                    properties: [],
                    requiredProperties: [],
                    items: null,
                    allowedValues: [],
                    additionalPropertiesAllowed: false,
                  },
                },
              ],
              requiredProperties: ['name'],
              items: null,
              allowedValues: [],
              additionalPropertiesAllowed: false,
            },
          },
          responsePolicy: {
            textAllowed: true,
            fileArtifactAllowed: false,
            mediaTypes: ['application/json'],
          },
          executionPolicy: {
            risk: 'write',
            approval: 'required',
            enforcementOwner: 'aevatar',
            allowedExecutionModes: ['interactive'],
          },
        },
        blockers: [
          {
            status: 'credential_connection_required',
            code: 'credential_connection_required',
            safeMessage: 'Reconnect the PostHog service.',
          },
        ],
        remediations: [
          {
            actionKind: 'connect_credential',
            label: 'Reconnect PostHog',
            trustedLocator: '/settings/connections/posthog',
          },
        ],
        sources: [],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    const result = await studioApi.inspectWorkflowCapabilityReadiness({
      scopeId: 'scope-alpha',
      executionMode: 'interactive',
      selector: {
        kind: 'nyxid_operation',
        userServiceId: 'us-posthog-alpha',
        endpointId: 'update-dashboard',
      },
    });

    expect(result.selectedOperation).toEqual(
      expect.objectContaining({
        httpMethod: 'PATCH',
        executionPolicy: expect.objectContaining({
          risk: 'write',
          approval: 'required',
        }),
      }),
    );
    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe(
      '/api/scopes/scope-alpha/workflow-capabilities:readiness',
    );
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({
      selector: {
        kind: 'nyxid_operation',
        userServiceId: 'us-posthog-alpha',
        endpointId: 'update-dashboard',
      },
      executionMode: 'interactive',
    });
  });

  it('rejects unsupported workflow capability selector kinds', async () => {
    global.fetch = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        capabilities: [
          {
            displayName: 'Future action',
            readOnly: true,
            destructive: false,
            selector: { kind: 'future_selector' },
            source: null,
          },
        ],
        candidateCount: 1,
        rejectedCount: 0,
        diagnostics: [],
      }),
    } as Response) as typeof global.fetch;

    await expect(
      studioApi.listWorkflowCapabilities('scope-alpha'),
    ).rejects.toThrow('selector.kind is not supported.');
  });

  it.each([
    {
      caseName: 'selected selector',
      executionMode: 'interactive',
      selectedSelector: {
        kind: 'nyxid_operation',
        userServiceId: 'us-posthog-alpha',
        endpointId: 'different-operation',
      },
      selectedOperation: null,
      expectedMessage: 'returned a different selectedSelector',
    },
    {
      caseName: 'selected operation',
      executionMode: 'interactive',
      selectedSelector: {
        kind: 'nyxid_operation',
        userServiceId: 'us-posthog-alpha',
        endpointId: 'update-dashboard',
      },
      selectedOperation: {
        userServiceId: 'us-posthog-alpha',
        endpointId: 'different-operation',
        serviceSlug: 'posthog',
        httpMethod: 'PATCH',
        pathTemplate: '/api/dashboards/{dashboard_id}',
        parameters: [],
        requestBody: null,
        responsePolicy: null,
        executionPolicy: null,
      },
      expectedMessage: 'returned a different selectedOperation',
    },
    {
      caseName: 'execution mode',
      executionMode: 'durable',
      selectedSelector: {
        kind: 'nyxid_operation',
        userServiceId: 'us-posthog-alpha',
        endpointId: 'update-dashboard',
      },
      selectedOperation: null,
      expectedMessage: 'returned a different executionMode',
    },
  ])('rejects readiness with a mismatched $caseName', async (response) => {
    global.fetch = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        executionMode: response.executionMode,
        status: 'ready',
        selectedSelector: response.selectedSelector,
        selectedOperation: response.selectedOperation,
        blockers: [],
        remediations: [],
        sources: [],
      }),
    } as Response) as typeof global.fetch;

    await expect(
      studioApi.inspectWorkflowCapabilityReadiness({
        scopeId: 'scope-alpha',
        executionMode: 'interactive',
        selector: {
          kind: 'nyxid_operation',
          userServiceId: 'us-posthog-alpha',
          endpointId: 'update-dashboard',
        },
      }),
    ).rejects.toThrow(response.expectedMessage);
  });

  it.each([
    {
      caseName: 'missing selected selector',
      status: 'credential_connection_required',
      selectedSelector: null,
      selectedOperation: null,
      expectedMessage: 'requires the requested selectedSelector',
    },
    {
      caseName: 'ready without an operation contract',
      status: 'ready',
      selectedSelector: {
        kind: 'nyxid_operation',
        userServiceId: 'us-posthog-alpha',
        endpointId: 'update-dashboard',
      },
      selectedOperation: null,
      expectedMessage: 'ready without a selectedOperation',
    },
  ])('rejects readiness with a $caseName', async (response) => {
    global.fetch = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        executionMode: 'interactive',
        status: response.status,
        selectedSelector: response.selectedSelector,
        selectedOperation: response.selectedOperation,
        blockers: [],
        remediations: [],
        sources: [],
      }),
    } as Response) as typeof global.fetch;

    await expect(
      studioApi.inspectWorkflowCapabilityReadiness({
        scopeId: 'scope-alpha',
        executionMode: 'interactive',
        selector: {
          kind: 'nyxid_operation',
          userServiceId: 'us-posthog-alpha',
          endpointId: 'update-dashboard',
        },
      }),
    ).rejects.toThrow(response.expectedMessage);
  });

  it('rejects member workflow binding without a stable workflow id', async () => {
    expect(() =>
      studioApi.bindMemberWorkflow({
        scopeId: 'scope-1',
        memberId: 'joker',
        displayName: 'joker',
        workflowId: ' ',
        revisionId: 'rev-alpha',
        workflowYamls: ['name: joker\nsteps: []\n'],
      }),
    ).toThrow('Workflow member binding requires a stable workflow id.');
  });

  it('saves and binds a published workflow using the dedicated save-and-bind endpoint', async () => {
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
      status: 202,
      json: async () => ({
        scopeId: 'scope-1',
        workflowId: 'wf-alpha',
        revisionId: 'rev-1',
        workflow: {
          scopeId: 'scope-1',
          workflowId: 'wf-alpha',
          revisionId: 'rev-1',
          readModelUrl: '/api/scopes/scope-1/workflows/wf-alpha',
          acceptanceStage: 'accepted',
          propagationStage: 'readmodel_propagating',
        },
        binding: {
          scopeId: 'scope-1',
          serviceId: 'svc-alpha',
          displayName: 'Workflow Alpha',
          revisionId: 'rev-1',
          implementationKind: 'workflow',
          targetKind: 'workflow',
          targetName: 'Workflow Alpha',
        },
        acceptanceStage: 'accepted',
        propagationStage: 'readmodel_propagating',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    const result = await studioApi.saveAndBindWorkflow({
      scopeId: 'scope-1',
      workflowId: 'wf-alpha',
      revisionId: 'rev-1',
      workflowYaml: 'name: Workflow Alpha\nsteps: []\n',
      workflowName: 'Workflow Alpha',
      displayName: 'Workflow Alpha',
      inlineWorkflowYamls: {},
      appId: 'studio',
      serviceId: 'svc-alpha',
      exposureDesired: true,
    });

    expect(result).toEqual(
      expect.objectContaining({
        scopeId: 'scope-1',
        workflowId: 'wf-alpha',
        revisionId: 'rev-1',
        acceptanceStage: 'accepted',
        propagationStage: 'readmodel_propagating',
      }),
    );

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/scopes/scope-1/workflows:save-and-bind');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({
      workflowId: 'wf-alpha',
      revisionId: 'rev-1',
      workflowYaml: 'name: Workflow Alpha\nsteps: []\n',
      workflowName: 'Workflow Alpha',
      displayName: 'Workflow Alpha',
      appId: 'studio',
      serviceId: 'svc-alpha',
      exposureDesired: true,
    });
  });

  it('publishes a workflow from an accepted upsert receipt without requiring a service identity', async () => {
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
      status: 202,
      json: async () => ({
        scopeId: 'scope-alpha',
        workflowId: 'wf-alpha',
        serviceKey: 'scope-alpha:default:default:svc-workflow-alpha',
        revisionId: 'rev-alpha',
        definitionActorIdPrefix: 'workflow-definition-alpha',
        expectedActorId: 'actor-alpha',
        expectedDeploymentId: 'deployment-alpha',
        acceptedAtUtc: '2026-08-07T00:00:00Z',
        commandHandles: [],
        readModelUrl: '/api/scopes/scope-alpha/workflows/wf-alpha',
        acceptanceStage: 'accepted',
        propagationStage: 'readmodel_propagating',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    const result = await studioApi.publishWorkflow({
      scopeId: 'scope-alpha',
      workflowId: 'wf-alpha',
      revisionId: 'rev-alpha',
      workflowYaml: 'name: Workflow Alpha\nsteps: []\n',
      workflowName: 'Workflow Alpha',
      displayName: 'Workflow Alpha',
      explicitRequestConfirmations: [],
    });

    expect(result).toEqual(
      expect.objectContaining({
        scopeId: 'scope-alpha',
        workflowId: 'wf-alpha',
        revisionId: 'rev-alpha',
      }),
    );
    expect(result).not.toHaveProperty('publishedServiceId');
    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/scopes/scope-alpha/workflows/wf-alpha');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(String(init?.body))).toEqual({
      revisionId: 'rev-alpha',
      workflowYaml: 'name: Workflow Alpha\nsteps: []\n',
      workflowName: 'Workflow Alpha',
      displayName: 'Workflow Alpha',
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
          agentKind: 'Tests.OrdersGAgent',
        },
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    const result = await studioApi.bindScopeGAgent({
      scopeId: 'scope-1',
      displayName: 'orders-gagent',
      agentKind: 'Tests.OrdersGAgent',
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
    expect(result.targetName).toBe('orders-gagent');
    expect(result.gAgent).toEqual({
      diagnosticClrTypeName: '',
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
        agentKind: 'Tests.OrdersGAgent',
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
        lastBinding: {
          publishedServiceId: 'member-joker',
          revisionId: 'rev-2',
          implementationKind: 'workflow',
          boundAt: '2026-03-26T08:00:00Z',
        },
        currentBindingRun: {
          bindingRunId: 'bind-1',
          status: 'platform_binding_pending',
          scopeId: 'scope-1',
          memberId: 'joker',
          stateVersion: 7,
          updatedAt: '2026-03-26T08:01:00Z',
        },
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.getMemberBinding('scope-1', 'joker'),
    ).resolves.toEqual(
      expect.objectContaining({
        lastBinding: expect.objectContaining({
          publishedServiceId: 'member-joker',
          revisionId: 'rev-2',
        }),
        currentBindingRun: expect.objectContaining({
          bindingRunId: 'bind-1',
          status: 'platform_binding_pending',
          stateVersion: 7,
        }),
      }),
    );

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/members/joker/binding',
      expect.objectContaining({
        credentials: 'same-origin',
      }),
    );
  });

  it('loads member binding run status with readmodel state version freshness', async () => {
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
        BindingRunId: 'bind-1',
        Status: 'platform_binding_pending',
        ScopeId: 'scope-1',
        MemberId: 'joker',
        StateVersion: 9,
        UpdatedAt: '2026-03-26T08:01:00Z',
        Result: {
          PublishedServiceId: 'svc-alpha',
          RevisionId: 'rev-alpha',
          ImplementationKind: 'workflow',
          ExpectedActorId: 'scope-workflow:scope-1:m-alpha',
        },
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.getMemberBindingRun('scope-1', 'joker', 'bind-1'),
    ).resolves.toEqual(
      expect.objectContaining({
        bindingRunId: 'bind-1',
        status: 'platform_binding_pending',
        stateVersion: 9,
        result: {
          publishedServiceId: 'svc-alpha',
          revisionId: 'rev-alpha',
          implementationKind: 'workflow',
          expectedActorId: 'scope-workflow:scope-1:m-alpha',
        },
      }),
    );

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/members/joker/binding-runs/bind-1',
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
            teamId: 't-alpha',
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
          displayName: 'joker',
          description: 'Support workflow member',
          implementationKind: 'workflow',
          lifecycleStage: 'bind_ready',
          publishedServiceId: 'member-joker',
          lastBoundRevisionId: 'rev-2',
          teamId: 't-alpha',
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

  it('lists studio teams from the team authority endpoint', async () => {
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
            teamId: 't-alpha',
            scopeId: 'scope-1',
            displayName: 'Alpha Team',
            description: 'Owns support workflows',
            lifecycleStage: 'active',
            memberCount: 2,
            createdAt: '2026-05-01T08:00:00Z',
            updatedAt: '2026-05-01T08:05:00Z',
            entryMemberId: 'member-team-alpha',
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
          teamId: 't-alpha',
          scopeId: 'scope-1',
          displayName: 'Alpha Team',
          description: 'Owns support workflows',
          lifecycleStage: 'active',
          memberCount: 2,
          createdAt: '2026-05-01T08:00:00Z',
          updatedAt: '2026-05-01T08:05:00Z',
          entryMemberId: 'member-team-alpha',
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

  it('loads workflow board snapshots from the scope read model endpoint', async () => {
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
        scopeId: 'scope-mainnet-01',
        generatedAt: '2026-06-24T13:24:16+00:00',
        watermark: 'workflow-board:v2:filterhash:facthash',
        counts: {
          running: 1,
          waiting: 2,
          failed: 0,
          retrying: 0,
          completed: 3,
        },
        teams: [
          {
            teamId: 't-alpha',
            teamName: 'Alpha Team',
            totalMemberCount: 8,
            members: [
              {
                memberId: 'm-alpha',
                displayName: 'Alpha member',
                executionAvailability: 'available',
                executionStatus: 'running',
                progress: {
                  completedSteps: 3,
                  totalSteps: 8,
                },
                completedNodes: [
                  {
                    nodeId: 'node-done',
                    name: 'Done',
                    completedAt: '2026-06-24T13:21:00+00:00',
                    durationMs: 120000,
                  },
                ],
                pendingNodes: [
                  {
                    nodeId: 'node-pending',
                    name: 'Pending',
                    status: 'pending',
                    reason: 'waiting for input',
                  },
                ],
                failedNodes: [
                  {
                    nodeId: 'node-failed',
                    name: 'Failed',
                    failedAt: '2026-06-24T13:22:00+00:00',
                  },
                ],
                workflowId: 'wf-alpha',
                workflowName: 'Workflow Alpha',
                publishedServiceId: 'svc-alpha',
                actorId: 'actor-alpha',
                roleSummary: 'role alpha',
                currentExecutionId: 'run-alpha',
                currentNode: {
                  nodeId: 'node-current',
                  name: 'Current',
                  status: 'running',
                  startedAt: '2026-06-24T13:20:00+00:00',
                  updatedAt: '2026-06-24T13:24:00+00:00',
                  durationMs: 240000,
                },
                lastNodeUpdatedAt: '2026-06-24T13:24:00+00:00',
              },
            ],
          },
        ],
        lastNodeUpdatedAt: '2026-06-24T13:24:00+00:00',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.getWorkflowBoardSnapshot('scope-mainnet-01', {
        take: 100,
        teamId: 't-alpha',
      }),
    ).resolves.toMatchObject({
      scopeId: 'scope-mainnet-01',
      counts: {
        running: 1,
        waiting: 2,
      },
      teams: [
        {
          teamId: 't-alpha',
          totalMemberCount: 8,
          members: [
            {
              memberId: 'm-alpha',
              executionStatus: 'running',
              currentExecutionId: 'run-alpha',
              progress: {
                completedSteps: 3,
                totalSteps: 8,
              },
              currentNode: {
                nodeId: 'node-current',
                status: 'running',
              },
            },
          ],
        },
      ],
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-mainnet-01/workflow-board/snapshot',
      expect.objectContaining({
        body: JSON.stringify({
          take: 100,
          teamId: 't-alpha',
        }),
        credentials: 'same-origin',
        method: 'POST',
      }),
    );
  });

  it('accepts nullable workflow board team totals from the backend contract', async () => {
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
        scopeId: 'scope-mainnet-01',
        generatedAt: '2026-06-24T13:24:16+00:00',
        watermark: 'workflow-board:v2:filterhash:facthash',
        counts: {
          running: 0,
          waiting: 0,
          failed: 0,
          retrying: 0,
          completed: 0,
        },
        teams: [
          {
            teamId: 't-alpha',
            teamName: 'Alpha Team',
            totalMemberCount: null,
            members: [],
          },
        ],
        lastNodeUpdatedAt: null,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.getWorkflowBoardSnapshot('scope-mainnet-01', {
        take: 100,
      }),
    ).resolves.toMatchObject({
      teams: [
        {
          teamId: 't-alpha',
          totalMemberCount: null,
        },
      ],
    });
  });

  it('gets a studio team summary from the team authority endpoint', async () => {
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
        teamId: 't-alpha',
        scopeId: 'scope-1',
        displayName: 'Alpha Team',
        description: 'Owns support workflows',
        lifecycleStage: 'active',
        memberCount: 2,
        createdAt: '2026-05-01T08:00:00Z',
        updatedAt: '2026-05-01T08:05:00Z',
        entryMemberId: 'member-team-alpha',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(studioApi.getTeam('scope-1', 't-alpha')).resolves.toEqual({
      teamId: 't-alpha',
      scopeId: 'scope-1',
      displayName: 'Alpha Team',
      description: 'Owns support workflows',
      lifecycleStage: 'active',
      memberCount: 2,
      createdAt: '2026-05-01T08:00:00Z',
      updatedAt: '2026-05-01T08:05:00Z',
      entryMemberId: 'member-team-alpha',
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/teams/t-alpha',
      expect.objectContaining({
        credentials: 'same-origin',
      }),
    );
  });

  it('creates, updates, archives, and lists members for studio teams', async () => {
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

    const teamResponse = {
      teamId: 't-alpha',
      scopeId: 'scope-1',
      displayName: 'Alpha Team',
      description: 'Owns support workflows',
      lifecycleStage: 'active',
      memberCount: 1,
      createdAt: '2026-05-01T08:00:00Z',
      updatedAt: '2026-05-01T08:05:00Z',
      entryMemberId: 'joker',
    };
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce({
        ok: true,
        status: 201,
        json: async () => teamResponse,
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 202,
        headers: new Headers({ 'content-type': 'application/json' }),
        json: async () => ({
          status: 'accepted',
          scopeId: 'scope-1',
          teamId: 't-alpha',
          commandId: 'cmd-team-update',
          correlationId: 'corr-team-update',
          ackedAt: '2026-05-01T08:06:00Z',
        }),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 202,
        headers: new Headers({ 'content-type': 'application/json' }),
        json: async () => ({
          status: 'accepted',
          scopeId: 'scope-1',
          teamId: 't-alpha',
          commandId: 'cmd-team-archive',
          correlationId: 'corr-team-archive',
          ackedAt: '2026-05-01T08:07:00Z',
        }),
      } as Response)
      .mockResolvedValueOnce({
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
              implementationRef: {
                implementationKind: 'workflow',
                workflowId: 'wf-joker',
                workflowRevision: 'rev-workflow-joker',
              },
              lifecycleStage: 'bind_ready',
              publishedServiceId: 'member-joker',
              lastBoundRevisionId: 'rev-2',
              teamId: 't-alpha',
              createdAt: '2026-04-27T08:00:00Z',
              updatedAt: '2026-04-27T08:05:00Z',
            },
          ],
          nextPageToken: null,
        }),
      } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.createTeam({
        scopeId: 'scope-1',
        displayName: 'Alpha Team',
        description: 'Owns support workflows',
        teamId: 't-alpha',
      }),
    ).resolves.toEqual(teamResponse);
    await expect(
      studioApi.updateTeam({
        scopeId: 'scope-1',
        teamId: 't-alpha',
        displayName: 'Alpha Ops',
        description: null,
      }),
    ).resolves.toEqual({
      status: 'accepted',
      scopeId: 'scope-1',
      teamId: 't-alpha',
      commandId: 'cmd-team-update',
      correlationId: 'corr-team-update',
      ackedAt: '2026-05-01T08:06:00Z',
    });
    await expect(studioApi.archiveTeam('scope-1', 't-alpha')).resolves.toEqual({
      status: 'accepted',
      scopeId: 'scope-1',
      teamId: 't-alpha',
      commandId: 'cmd-team-archive',
      correlationId: 'corr-team-archive',
      ackedAt: '2026-05-01T08:07:00Z',
    });
    await expect(
      studioApi.listTeamMembers('scope-1', 't-alpha'),
    ).resolves.toEqual({
      scopeId: 'scope-1',
      members: [
        {
          memberId: 'joker',
          scopeId: 'scope-1',
          displayName: 'joker',
          description: 'Support workflow member',
          implementationKind: 'workflow',
          implementationRef: {
            implementationKind: 'workflow',
            workflowId: 'wf-joker',
            workflowRevision: 'rev-workflow-joker',
            scriptId: null,
            scriptRevision: null,
            agentKind: null,
            diagnosticActorTypeName: null,
          },
          lifecycleStage: 'bind_ready',
          publishedServiceId: 'member-joker',
          lastBoundRevisionId: 'rev-2',
          teamId: 't-alpha',
          createdAt: '2026-04-27T08:00:00Z',
          updatedAt: '2026-04-27T08:05:00Z',
        },
      ],
      nextPageToken: null,
    });

    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      '/api/scopes/scope-1/teams',
      expect.objectContaining({
        body: JSON.stringify({
          displayName: 'Alpha Team',
          description: 'Owns support workflows',
          teamId: 't-alpha',
        }),
        credentials: 'same-origin',
        method: 'POST',
      }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      '/api/scopes/scope-1/teams/t-alpha',
      expect.objectContaining({
        body: JSON.stringify({
          displayName: 'Alpha Ops',
          description: null,
        }),
        method: 'PATCH',
      }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      '/api/scopes/scope-1/teams/t-alpha/archive',
      expect.objectContaining({
        method: 'POST',
      }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      4,
      '/api/scopes/scope-1/teams/t-alpha/members',
      expect.objectContaining({
        credentials: 'same-origin',
      }),
    );
  });

  it('accepts legacy studio team update summaries without treating them as decode failures', async () => {
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

    const fetchMock = jest.fn().mockResolvedValueOnce({
      ok: true,
      status: 200,
      json: async () => ({
        teamId: 't-alpha',
        scopeId: 'scope-1',
        displayName: 'Alpha Ops',
        description: '',
        lifecycleStage: 'active',
        memberCount: 1,
        createdAt: '2026-05-01T08:00:00Z',
        updatedAt: '2026-05-01T08:06:00Z',
        entryMemberId: 'joker',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.updateTeam({
        scopeId: 'scope-1',
        teamId: 't-alpha',
        displayName: 'Alpha Ops',
        description: null,
      }),
    ).resolves.toEqual({
      status: 'accepted',
      scopeId: 'scope-1',
      teamId: 't-alpha',
      commandId: null,
      correlationId: null,
      ackedAt: null,
    });
  });

  it('sets and clears a studio team entry member', async () => {
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

    const teamResponse = {
      teamId: 't-alpha',
      scopeId: 'scope-1',
      displayName: 'Alpha Team',
      description: 'Owns support workflows',
      entryMemberId: 'joker',
      lifecycleStage: 'active',
      memberCount: 1,
      createdAt: '2026-05-01T08:00:00Z',
      updatedAt: '2026-05-01T08:05:00Z',
    };
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => teamResponse,
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({
          ...teamResponse,
          entryMemberId: null,
        }),
      } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.setTeamEntryMember('scope-1', 't-alpha', 'joker'),
    ).resolves.toEqual(teamResponse);
    await expect(
      studioApi.clearTeamEntryMember('scope-1', 't-alpha'),
    ).resolves.toEqual({
      ...teamResponse,
      entryMemberId: null,
    });

    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      '/api/scopes/scope-1/teams/t-alpha/entry-member',
      expect.objectContaining({
        body: JSON.stringify({
          memberId: 'joker',
        }),
        credentials: 'same-origin',
        method: 'PUT',
      }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      '/api/scopes/scope-1/teams/t-alpha/entry-member',
      expect.objectContaining({
        credentials: 'same-origin',
        method: 'DELETE',
      }),
    );
  });

  it('accepts asynchronous studio team entry member updates through the team authority endpoint', async () => {
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

    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce({
        ok: true,
        status: 202,
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 204,
      } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.setTeamEntryMember(
        ' scope-1 ',
        ' t-alpha ',
        ' member-team-alpha ',
      ),
    ).resolves.toBeUndefined();
    await expect(
      studioApi.clearTeamEntryMember(' scope-1 ', ' t-alpha '),
    ).resolves.toBeUndefined();

    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      '/api/scopes/scope-1/teams/t-alpha/entry-member',
      expect.objectContaining({
        body: JSON.stringify({
          memberId: 'member-team-alpha',
        }),
        credentials: 'same-origin',
        method: 'PUT',
      }),
    );
    expect(
      new Headers(fetchMock.mock.calls[0][1].headers).get('Authorization'),
    ).toBe('Bearer access-token');
    expect(
      new Headers(fetchMock.mock.calls[0][1].headers).get('Content-Type'),
    ).toBe('application/json');
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      '/api/scopes/scope-1/teams/t-alpha/entry-member',
      expect.objectContaining({
        credentials: 'same-origin',
        method: 'DELETE',
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
        displayName: 'joker',
        description: 'Support workflow member',
        implementationKind: 'workflow',
        lifecycleStage: 'bind_ready',
        publishedServiceId: 'member-joker',
        lastBoundRevisionId: 'rev-2',
        teamId: null,
        createdAt: '2026-04-27T08:00:00Z',
        updatedAt: '2026-04-27T08:05:00Z',
      },
      implementationRef: {
        implementationKind: 'workflow',
        workflowId: 'joker',
        workflowRevision: 'rev-2',
        scriptId: null,
        scriptRevision: null,
        agentKind: null,
        diagnosticActorTypeName: null,
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
      }),
    ).resolves.toEqual({
      memberId: 'orders-draft',
      scopeId: 'scope-1',
      displayName: 'orders-draft',
      description: '',
      implementationKind: 'workflow',
      lifecycleStage: 'created',
      publishedServiceId: 'member-orders-draft',
      lastBoundRevisionId: null,
      teamId: null,
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
        }),
      }),
    );
  });

  it('does not send a caller-derived memberId when creating a workflow member', async () => {
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
        memberId: 'm-generated-member',
        scopeId: 'scope-1',
        displayName: 'Untitled member',
        description: '',
        implementationKind: 'workflow',
        lifecycleStage: 'created',
        publishedServiceId: 'member-m-generated-member',
        lastBoundRevisionId: null,
        teamId: 'team-1',
        createdAt: '2026-04-27T08:10:00Z',
        updatedAt: '2026-04-27T08:10:00Z',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await studioApi.createMember({
      scopeId: 'scope-1',
      displayName: 'Untitled member',
      implementationKind: 'workflow',
      teamId: 'team-1',
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/members',
      expect.objectContaining({
        body: JSON.stringify({
          displayName: 'Untitled member',
          implementationKind: 'workflow',
          teamId: 'team-1',
        }),
      }),
    );
    expect(JSON.parse(fetchMock.mock.calls[0][1].body)).not.toHaveProperty(
      'memberId',
    );
  });

  it('requires the explicit createMemberWithId helper when callers own the member id', async () => {
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
        memberId: 'script-alpha',
        scopeId: 'scope-1',
        displayName: 'Script Alpha',
        description: '',
        implementationKind: 'script',
        lifecycleStage: 'created',
        publishedServiceId: 'member-script-alpha',
        lastBoundRevisionId: null,
        createdAt: '2026-04-27T08:10:00Z',
        updatedAt: '2026-04-27T08:10:00Z',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await studioApi.createMemberWithId({
      scopeId: 'scope-1',
      memberId: 'script-alpha',
      displayName: 'Script Alpha',
      implementationKind: 'script',
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/members',
      expect.objectContaining({
        body: JSON.stringify({
          displayName: 'Script Alpha',
          implementationKind: 'script',
          memberId: 'script-alpha',
        }),
      }),
    );
  });

  it('assigns an existing workflow member to a team with the member patch endpoint', async () => {
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
      status: 202,
      json: async () => ({
        status: 'accepted',
        scopeId: 'scope-1',
        memberId: 'orders-draft',
        ackedAt: '2026-04-27T08:11:00Z',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.updateMemberTeamAssignment({
        scopeId: 'scope-1',
        memberId: 'orders-draft',
        teamId: 'team-1',
      }),
    ).resolves.toEqual({
      status: 'accepted',
      scopeId: 'scope-1',
      memberId: 'orders-draft',
      ackedAt: '2026-04-27T08:11:00Z',
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/members/orders-draft',
      expect.objectContaining({
        credentials: 'same-origin',
        method: 'PATCH',
        body: JSON.stringify({
          teamId: 'team-1',
        }),
      }),
    );
  });

  it('renames a workflow member with the member patch endpoint', async () => {
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
      status: 202,
      json: async () => ({
        status: 'accepted',
        scopeId: 'scope-1',
        memberId: 'm-alpha',
        ackedAt: '2026-04-27T08:12:00Z',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.updateMemberDisplayName({
        scopeId: 'scope-1',
        memberId: 'm-alpha',
        displayName: '  Workflow Alpha Renamed  ',
      }),
    ).resolves.toEqual({
      status: 'accepted',
      scopeId: 'scope-1',
      memberId: 'm-alpha',
      ackedAt: '2026-04-27T08:12:00Z',
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/members/m-alpha',
      expect.objectContaining({
        credentials: 'same-origin',
        method: 'PATCH',
        body: JSON.stringify({
          displayName: 'Workflow Alpha Renamed',
        }),
      }),
    );
  });

  it('updates a member workflow implementation ref with the member patch endpoint', async () => {
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
      status: 202,
      json: async () => ({
        status: 'accepted',
        scopeId: 'scope-1',
        memberId: 'm-alpha',
        ackedAt: '2026-04-27T08:11:00Z',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.updateMemberImplementationRef({
        scopeId: 'scope-1',
        memberId: 'm-alpha',
        implementationRef: {
          implementationKind: 'workflow',
          workflowId: 'wf-alpha',
        },
      }),
    ).resolves.toEqual({
      status: 'accepted',
      scopeId: 'scope-1',
      memberId: 'm-alpha',
      ackedAt: '2026-04-27T08:11:00Z',
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/members/m-alpha',
      expect.objectContaining({
        credentials: 'same-origin',
        method: 'PATCH',
        body: JSON.stringify({
          implementationRef: {
            implementationKind: 'workflow',
            workflowId: 'wf-alpha',
          },
        }),
      }),
    );
  });

  it('deletes an existing member with the member delete endpoint', async () => {
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
      status: 202,
      json: async () => ({
        status: 'delete_accepted',
        scopeId: 'scope-1',
        memberId: 'm-alpha',
        ackedAt: '2026-07-09T08:12:00Z',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.deleteMember({
        scopeId: 'scope-1',
        memberId: 'm-alpha',
      }),
    ).resolves.toEqual({
      status: 'delete_accepted',
      scopeId: 'scope-1',
      memberId: 'm-alpha',
      ackedAt: '2026-07-09T08:12:00Z',
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/members/m-alpha',
      expect.objectContaining({
        credentials: 'same-origin',
        method: 'DELETE',
      }),
    );
  });

  it('synthesizes a member command response from legacy member detail for team patch responses', async () => {
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
      status: 202,
      json: async () => ({
        summary: {
          memberId: 'm-alpha',
          scopeId: 'scope-1',
          displayName: 'Workflow Alpha',
          description: '',
          implementationKind: 'workflow',
          implementationRef: {
            implementationKind: 'workflow',
            workflowId: 'wf-alpha',
            workflowRevision: 'rev-wf-alpha',
          },
          lifecycleStage: 'build_ready',
          publishedServiceId: 'svc-alpha',
          lastBoundRevisionId: null,
          teamId: 'team-1',
          createdAt: '2026-04-27T08:00:00Z',
          updatedAt: '2026-04-27T08:11:00Z',
        },
        implementationRef: {
          implementationKind: 'workflow',
          workflowId: 'wf-alpha',
          workflowRevision: 'rev-wf-alpha',
        },
        lastBinding: null,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.updateMemberTeamAssignment({
        scopeId: 'scope-1',
        memberId: 'm-alpha',
        teamId: 'team-1',
      }),
    ).resolves.toEqual({
      status: 'accepted',
      scopeId: 'scope-1',
      memberId: 'm-alpha',
      ackedAt: null,
    });
  });

  it('synthesizes a member command response from legacy member detail for implementation ref patch responses', async () => {
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
          memberId: 'm-alpha',
          scopeId: 'scope-1',
          displayName: 'Workflow Alpha',
          description: '',
          implementationKind: 'workflow',
          implementationRef: {
            implementationKind: 'workflow',
            workflowId: 'wf-alpha',
            workflowRevision: 'rev-wf-alpha',
          },
          lifecycleStage: 'build_ready',
          publishedServiceId: 'svc-alpha',
          lastBoundRevisionId: null,
          teamId: 'team-1',
          createdAt: '2026-04-27T08:00:00Z',
          updatedAt: '2026-04-27T08:11:00Z',
        },
        implementationRef: {
          implementationKind: 'workflow',
          workflowId: 'wf-alpha',
          workflowRevision: 'rev-wf-alpha',
        },
        lastBinding: null,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.updateMemberImplementationRef({
        scopeId: 'scope-1',
        memberId: 'm-alpha',
        implementationRef: {
          implementationKind: 'workflow',
          workflowId: 'wf-alpha',
          workflowRevision: 'rev-wf-alpha',
        },
      }),
    ).resolves.toEqual({
      status: 'accepted',
      scopeId: 'scope-1',
      memberId: 'm-alpha',
      ackedAt: null,
    });
  });

  it('synthesizes a member command response when legacy detail reflects the display name patch', async () => {
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
          memberId: 'm-alpha',
          scopeId: 'scope-1',
          displayName: 'Workflow Alpha Renamed',
          description: '',
          implementationKind: 'workflow',
          implementationRef: {
            implementationKind: 'workflow',
            workflowId: 'wf-alpha',
            workflowRevision: 'rev-wf-alpha',
          },
          lifecycleStage: 'build_ready',
          publishedServiceId: 'svc-alpha',
          lastBoundRevisionId: null,
          teamId: 'team-1',
          createdAt: '2026-04-27T08:00:00Z',
          updatedAt: '2026-04-27T08:12:00Z',
        },
        implementationRef: {
          implementationKind: 'workflow',
          workflowId: 'wf-alpha',
          workflowRevision: 'rev-wf-alpha',
        },
        lastBinding: null,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.updateMemberDisplayName({
        scopeId: 'scope-1',
        memberId: 'm-alpha',
        displayName: '  Workflow Alpha Renamed  ',
      }),
    ).resolves.toEqual({
      status: 'accepted',
      scopeId: 'scope-1',
      memberId: 'm-alpha',
      ackedAt: null,
    });
  });

  it('synthesizes a member command response when legacy detail ignores the display name patch', async () => {
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
          memberId: 'm-alpha',
          scopeId: 'scope-1',
          displayName: 'Workflow Alpha',
          description: '',
          implementationKind: 'workflow',
          implementationRef: {
            implementationKind: 'workflow',
            workflowId: 'wf-alpha',
            workflowRevision: 'rev-wf-alpha',
          },
          lifecycleStage: 'build_ready',
          publishedServiceId: 'svc-alpha',
          lastBoundRevisionId: null,
          teamId: 'team-1',
          createdAt: '2026-04-27T08:00:00Z',
          updatedAt: '2026-04-27T08:12:00Z',
        },
        implementationRef: {
          implementationKind: 'workflow',
          workflowId: 'wf-alpha',
          workflowRevision: 'rev-wf-alpha',
        },
        lastBinding: null,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.updateMemberDisplayName({
        scopeId: 'scope-1',
        memberId: 'm-alpha',
        displayName: 'Workflow Alpha Renamed',
      }),
    ).resolves.toEqual({
      status: 'accepted',
      scopeId: 'scope-1',
      memberId: 'm-alpha',
      ackedAt: null,
    });
  });

  it('removes an existing workflow member from a team with explicit null teamId', async () => {
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
      status: 202,
      json: async () => ({
        status: 'accepted',
        scopeId: 'scope-1',
        memberId: 'orders-draft',
        ackedAt: '2026-04-27T08:11:00Z',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      studioApi.updateMemberTeamAssignment({
        scopeId: 'scope-1',
        memberId: 'orders-draft',
        teamId: null,
      }),
    ).resolves.toEqual({
      status: 'accepted',
      scopeId: 'scope-1',
      memberId: 'orders-draft',
      ackedAt: '2026-04-27T08:11:00Z',
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/members/orders-draft',
      expect.objectContaining({
        credentials: 'same-origin',
        method: 'PATCH',
        body: JSON.stringify({
          teamId: null,
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
