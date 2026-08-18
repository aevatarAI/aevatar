import { persistAuthSession } from '@/shared/auth/session';
import { AIWorkspaceApiError, aiWorkspaceApi } from './aiWorkspaceApi';

const contextPayload = {
  scopeId: 'scope-alpha',
  consistency: 'independent_read_models',
  pages: {
    overview: '/ai',
    chat: '/ai/chat',
    agents: '/ai/agents',
    models: '/ai/models',
  },
  apis: {
    overview: '/api/ai/overview',
    chat: '/api/chat',
    agents: '/api/ai/agents',
    ownedAgentProfiles: '/api/scopes/scope-alpha/agent-profiles',
    systemAgentProfiles: '/api/agent-profiles/system',
    models: '/api/ai/models',
    personalModelSettings: '/api/user-config/llm',
    scopeModelCatalog: '/api/scopes/scope-alpha/llm-model-catalog',
    activity: '/api/ai/activity',
    conversations: '/api/ai/activity/conversations',
    runs: '/api/ai/activity/runs',
  },
  features: {
    overview: {
      availability: 'available',
      page: '/ai',
      api: '/api/ai/overview',
    },
    chat: { availability: 'available', page: '/ai/chat', api: '/api/chat' },
    agents: {
      availability: 'available',
      page: '/ai/agents',
      api: '/api/ai/agents',
    },
    models: {
      availability: 'available',
      page: '/ai/models',
      api: '/api/ai/models',
    },
  },
};

const agentsPayload = {
  consistency: 'independent_read_models',
  owned: {
    source: 'agent_profile_catalog',
    ownerKind: 'scope',
    scopeId: 'scope-alpha',
    availability: 'available',
    items: [
      {
        profileId: 'profile-alpha',
        profileSlug: 'writer',
        displayName: 'Writer',
        purpose: 'Draft concise release notes.',
        publishedRevision: 3,
        publishedSnapshotSha256: 'abc123',
        published: true,
        status: 'active',
      },
      {
        profileId: 'profile-draft',
        profileSlug: 'draft-agent',
        displayName: '',
        purpose: 'Waiting for its first publication.',
        publishedRevision: 0,
        publishedSnapshotSha256: null,
        published: false,
        status: 'provisioning',
      },
    ],
    nextCursor: 'owned-next',
    totalCount: 2,
    authorityStateVersion: 17,
    updatedAtUtc: '2026-08-18T08:00:00Z',
  },
  systemTemplates: {
    source: 'agent_profile_catalog',
    ownerKind: 'system',
    scopeId: null,
    availability: 'not_materialized',
    items: [],
    nextCursor: null,
    totalCount: 0,
    authorityStateVersion: null,
    updatedAtUtc: null,
  },
};

const overviewPayload = {
  consistency: 'independent_read_models',
  agents: {
    owned: {
      source: 'agent_profile_catalog',
      availability: 'available',
      itemCount: 2,
      authorityStateVersion: 17,
      updatedAtUtc: '2026-08-18T08:00:00Z',
      error: null,
    },
    systemTemplates: {
      source: 'agent_profile_catalog',
      availability: 'not_materialized',
      itemCount: null,
      authorityStateVersion: null,
      updatedAtUtc: null,
      error: null,
    },
  },
  recentConversations: {
    source: 'chat_history',
    scopeId: 'scope-alpha',
    availability: 'available',
    items: [
      {
        conversationId: 'conversation-alpha',
        title: 'Release planning',
        serviceId: 'service-alpha',
        serviceKind: 'agent',
        createdAtUtc: '2026-08-18T07:00:00Z',
        updatedAtUtc: '2026-08-18T08:10:00Z',
        messageCount: 6,
        llmRoute: 'route-alpha',
        llmModel: 'gpt-alpha',
        taskStatus: 'running',
        attentionKind: null,
        attentionSinceUtc: null,
        activeStepSummary: 'Drafting release notes',
        authorityStateVersion: 9,
      },
    ],
    nextCursor: null,
    error: null,
  },
  recentRuns: {
    source: 'workflow_run_observatory',
    scopeId: 'scope-alpha',
    availability: 'available',
    items: [
      {
        runId: 'run-alpha',
        workflowId: 'wf-alpha',
        workflowName: 'Release workflow',
        status: 'failed',
        runOrigin: 'chat',
        success: false,
        inputSummary: 'Prepare the release notes',
        currentStep: {
          stepId: 'step-alpha',
          inputSummary: 'Draft release notes',
          availability: 'available',
        },
        firstFailure: {
          stepId: 'step-alpha',
          message: 'Release validation failed.',
          availability: 'available',
        },
        waiting: null,
        startedAtUtc: '2026-08-18T08:00:00Z',
        completedAtUtc: null,
        updatedAtUtc: '2026-08-18T08:12:00Z',
        durationMs: 720000,
        authorityStateVersion: 11,
      },
    ],
    nextCursor: null,
    hasMore: false,
    totalCount: 1,
    error: null,
  },
};

function jsonResponse(payload: unknown): Response {
  return {
    json: async () => payload,
    ok: true,
    status: 200,
    statusText: 'OK',
  } as Response;
}

describe('aiWorkspaceApi', () => {
  const originalFetch = global.fetch;

  beforeEach(() => {
    window.localStorage.clear();
    jest.spyOn(Date, 'now').mockReturnValue(1_700_000_000_000);
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        expiresAt: Date.now() + 3_600_000,
        expiresIn: 3_600,
        tokenType: 'Bearer',
      },
      user: {
        sub: 'user-alpha',
      },
    });
  });

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
    window.localStorage.clear();
  });

  it('loads only the page and API capabilities declared by the AI context', async () => {
    const fetchMock = jest.fn().mockResolvedValue(jsonResponse(contextPayload));
    global.fetch = fetchMock as typeof global.fetch;

    await expect(aiWorkspaceApi.getContext()).resolves.toEqual({
      scopeId: 'scope-alpha',
      consistency: 'independent_read_models',
      pages: {
        overview: '/ai',
        chat: '/ai/chat',
        agents: '/ai/agents',
        models: '/ai/models',
      },
      apis: {
        overview: '/api/ai/overview',
        chat: '/api/chat',
        agents: '/api/ai/agents',
        ownedAgentProfiles: '/api/scopes/scope-alpha/agent-profiles',
        systemAgentProfiles: '/api/agent-profiles/system',
        models: '/api/ai/models',
        personalModelSettings: '/api/user-config/llm',
        scopeModelCatalog: '/api/scopes/scope-alpha/llm-model-catalog',
        activity: '/api/ai/activity',
        conversations: '/api/ai/activity/conversations',
        runs: '/api/ai/activity/runs',
      },
      features: {
        overview: {
          availability: 'available',
          page: '/ai',
          api: '/api/ai/overview',
        },
        chat: {
          availability: 'available',
          page: '/ai/chat',
          api: '/api/chat',
        },
        agents: {
          availability: 'available',
          page: '/ai/agents',
          api: '/api/ai/agents',
        },
        models: {
          availability: 'available',
          page: '/ai/models',
          api: '/api/ai/models',
        },
      },
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/ai/context');
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
  });

  it('queries owned and system Agent Profiles without accepting a scope input', async () => {
    const fetchMock = jest.fn().mockResolvedValue(jsonResponse(agentsPayload));
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      aiWorkspaceApi.getAgents({
        ownedCursor: ' owned-cursor ',
        systemCursor: ' system-cursor ',
        take: 25,
      }),
    ).resolves.toMatchObject({
      owned: {
        ownerKind: 'scope',
        scopeId: 'scope-alpha',
        totalCount: 2,
      },
      systemTemplates: {
        ownerKind: 'system',
        scopeId: null,
        totalCount: 0,
      },
    });

    const [input] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe(
      '/api/ai/agents?ownedCursor=owned-cursor&systemCursor=system-cursor&take=25',
    );
    expect(input).not.toContain('scopeId');
  });

  it('preserves an empty display name for an unpublished Agent Profile', async () => {
    global.fetch = jest
      .fn()
      .mockResolvedValue(jsonResponse(agentsPayload)) as typeof global.fetch;

    await expect(aiWorkspaceApi.getAgents()).resolves.toMatchObject({
      owned: {
        items: [
          { profileId: 'profile-alpha', displayName: 'Writer' },
          {
            profileId: 'profile-draft',
            profileSlug: 'draft-agent',
            displayName: '',
            publishedRevision: 0,
            published: false,
          },
        ],
      },
    });
  });

  it('loads the Overview window with only a bounded take query', async () => {
    const fetchMock = jest
      .fn()
      .mockResolvedValue(jsonResponse(overviewPayload));
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      aiWorkspaceApi.getOverview({ take: 5 }),
    ).resolves.toMatchObject({
      agents: {
        owned: {
          availability: 'available',
          itemCount: 2,
        },
        systemTemplates: {
          availability: 'not_materialized',
          itemCount: null,
        },
      },
      recentConversations: {
        scopeId: 'scope-alpha',
        items: [{ conversationId: 'conversation-alpha' }],
      },
      recentRuns: {
        scopeId: 'scope-alpha',
        items: [
          {
            runId: 'run-alpha',
            firstFailure: { message: 'Release validation failed.' },
          },
        ],
      },
    });

    const [input] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/ai/overview?take=5');
    expect(input).not.toContain('scopeId');
  });

  it('rejects an Overview source that does not match its typed authority', async () => {
    global.fetch = jest.fn().mockResolvedValue(
      jsonResponse({
        ...overviewPayload,
        recentRuns: {
          ...overviewPayload.recentRuns,
          source: 'chat_history',
        },
      }),
    ) as typeof global.fetch;

    await expect(aiWorkspaceApi.getOverview({ take: 5 })).rejects.toThrow(
      'AIWorkspaceOverview.recentRuns.source must be one of workflow_run_observatory',
    );
  });

  it('accepts legacy activity whose optional display fields are unstamped', async () => {
    global.fetch = jest.fn().mockResolvedValue(
      jsonResponse({
        ...overviewPayload,
        recentConversations: {
          ...overviewPayload.recentConversations,
          items: [
            {
              ...overviewPayload.recentConversations.items[0],
              serviceId: '',
              serviceKind: '',
            },
          ],
        },
        recentRuns: {
          ...overviewPayload.recentRuns,
          items: [
            {
              ...overviewPayload.recentRuns.items[0],
              runOrigin: '',
              status: '',
              workflowName: '',
            },
          ],
        },
      }),
    ) as typeof global.fetch;

    await expect(
      aiWorkspaceApi.getOverview({ take: 5 }),
    ).resolves.toMatchObject({
      recentConversations: {
        items: [{ serviceId: '', serviceKind: '' }],
      },
      recentRuns: {
        items: [{ runOrigin: '', status: '', workflowName: '' }],
      },
    });
  });

  it('rejects an Agent Profile collection returned under the wrong owner', async () => {
    const fetchMock = jest.fn().mockResolvedValue(
      jsonResponse({
        ...agentsPayload,
        owned: {
          ...agentsPayload.owned,
          ownerKind: 'system',
        },
      }),
    );
    global.fetch = fetchMock as typeof global.fetch;

    await expect(aiWorkspaceApi.getAgents()).rejects.toThrow(
      'AIWorkspaceAgents.owned.ownerKind must be scope',
    );
  });

  it('preserves backend status and code for actionable failures', async () => {
    global.fetch = jest.fn().mockResolvedValue({
      ok: false,
      status: 503,
      statusText: 'Service Unavailable',
      text: async () =>
        JSON.stringify({
          code: 'AGENT_PROFILE_READ_MODEL_UNAVAILABLE',
          message: 'Agent profiles are temporarily unavailable.',
        }),
    } as Response) as typeof global.fetch;

    const request = aiWorkspaceApi.getAgents();
    await expect(request).rejects.toMatchObject({
      code: 'AGENT_PROFILE_READ_MODEL_UNAVAILABLE',
      message: 'Agent profiles are temporarily unavailable.',
      status: 503,
    });
    await expect(request).rejects.toBeInstanceOf(AIWorkspaceApiError);
  });
});
