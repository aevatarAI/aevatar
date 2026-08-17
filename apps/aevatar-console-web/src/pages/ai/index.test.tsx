import { screen, within } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import React from 'react';
import { persistAuthSession } from '@/shared/auth/session';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import AIOverviewPage from './index';

jest.mock('@/shared/api/aiWorkspaceApi', () => {
  const actual = jest.requireActual('@/shared/api/aiWorkspaceApi');
  return {
    ...actual,
    aiWorkspaceApi: {
      getContext: jest.fn(),
      getOverview: jest.fn(),
    },
  };
});

const { aiWorkspaceApi: mockAIWorkspaceApi } = jest.requireMock(
  '@/shared/api/aiWorkspaceApi',
) as {
  aiWorkspaceApi: {
    getContext: jest.Mock;
    getOverview: jest.Mock;
  };
};

const context = {
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

const overview = {
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
        status: 'running',
        runOrigin: 'chat',
        success: null,
        inputSummary: 'Prepare the release notes',
        currentStep: {
          stepId: 'step-alpha',
          inputSummary: 'Draft release notes',
          availability: 'available',
        },
        firstFailure: null,
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

describe('AIOverviewPage', () => {
  const scrollIntoViewDescriptor = Object.getOwnPropertyDescriptor(
    Element.prototype,
    'scrollIntoView',
  );

  beforeEach(() => {
    jest.clearAllMocks();
    Object.defineProperty(Element.prototype, 'scrollIntoView', {
      configurable: true,
      value: jest.fn(),
      writable: true,
    });
    setLocale('en-US', false);
    window.history.replaceState({}, '', '/ai');
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
        email: 'owner@example.com',
        sub: 'user-alpha',
      },
    });
    mockAIWorkspaceApi.getContext.mockResolvedValue(context);
    mockAIWorkspaceApi.getOverview.mockResolvedValue(overview);
  });

  afterEach(() => {
    jest.restoreAllMocks();
    if (scrollIntoViewDescriptor) {
      Object.defineProperty(
        Element.prototype,
        'scrollIntoView',
        scrollIntoViewDescriptor,
      );
    } else {
      Reflect.deleteProperty(Element.prototype, 'scrollIntoView');
    }
    window.localStorage.clear();
  });

  it('renders the three independent Overview sources and only real actions', async () => {
    window.history.replaceState({}, '', '/ai/');
    renderWithQueryClient(React.createElement(AIOverviewPage));

    expect(
      await screen.findByRole('heading', { name: 'Overview' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Agent readiness' }),
    ).toBeTruthy();
    expect(
      screen.getByRole('heading', { name: 'Recent conversations' }),
    ).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Recent runs' })).toBeTruthy();
    expect(screen.getByText('Release workflow')).toBeTruthy();
    expect(screen.getByText('State version 17')).toBeTruthy();
    expect(screen.getByText('Not ready')).toBeTruthy();

    const newChat = screen.getByRole('link', { name: 'New Chat' });
    expect(newChat).toHaveAttribute('href', '/ai/chat');
    const openAgents = screen.getByRole('link', { name: 'Open Agents' });
    expect(openAgents).toHaveAttribute('href', '/ai/agents');
    expect(
      screen.getByRole('link', { name: 'Open conversation Release planning' }),
    ).toHaveAttribute('href', '/ai/chat?conversationId=conversation-alpha');
    expect(screen.queryByRole('button', { name: /create agent/i })).toBeNull();

    const navigation = screen.getByRole('navigation', {
      name: 'AI workspace navigation',
    });
    for (const item of ['Overview', 'Chat', 'Agents', 'Models']) {
      expect(within(navigation).getByText(item)).toBeTruthy();
    }
    expect(
      within(navigation).getByRole('link', { name: 'Overview' }),
    ).toHaveAttribute('aria-current', 'page');
    expect(Element.prototype.scrollIntoView).toHaveBeenCalledWith({
      block: 'nearest',
      inline: 'nearest',
    });
    expect(mockAIWorkspaceApi.getOverview).toHaveBeenCalledWith(
      { take: 5 },
      expect.any(AbortSignal),
    );
  });

  it('keeps healthy sources visible when other sources are unavailable', async () => {
    mockAIWorkspaceApi.getOverview.mockResolvedValueOnce({
      ...overview,
      agents: {
        ...overview.agents,
        owned: {
          ...overview.agents.owned,
          availability: 'unavailable',
          itemCount: null,
          authorityStateVersion: null,
          updatedAtUtc: null,
          error: {
            code: 'OWNED_AGENT_PROFILES_UNAVAILABLE',
            message: 'Owned Agent Profiles are temporarily unavailable.',
          },
        },
      },
      recentRuns: {
        ...overview.recentRuns,
        availability: 'unavailable',
        items: [],
        totalCount: null,
        error: {
          code: 'WORKFLOW_RUNS_UNAVAILABLE',
          message: 'Workflow runs are temporarily unavailable.',
        },
      },
    });

    renderWithQueryClient(React.createElement(AIOverviewPage));

    expect(
      await screen.findByText(
        'Owned Agent Profiles are temporarily unavailable.',
      ),
    ).toBeTruthy();
    expect(screen.getByText('Release planning')).toBeTruthy();
    expect(
      screen.getByText('Workflow runs are temporarily unavailable.'),
    ).toBeTruthy();
  });

  it('rejects activity returned for a different authenticated scope', async () => {
    mockAIWorkspaceApi.getOverview.mockResolvedValueOnce({
      ...overview,
      recentRuns: {
        ...overview.recentRuns,
        scopeId: 'scope-other',
      },
    });

    renderWithQueryClient(React.createElement(AIOverviewPage));

    expect(
      await screen.findByText('Overview scope mismatch'),
    ).toBeInTheDocument();
    expect(screen.queryByText('Release workflow')).toBeNull();
  });

  it('does not query Overview when its capability contract is incomplete', async () => {
    mockAIWorkspaceApi.getContext.mockResolvedValueOnce({
      ...context,
      apis: {
        ...context.apis,
        overview: undefined,
      },
    });

    renderWithQueryClient(React.createElement(AIOverviewPage));

    expect(await screen.findByText('Overview not available')).toBeTruthy();
    expect(mockAIWorkspaceApi.getOverview).not.toHaveBeenCalled();
  });

  it('renders explicit fallbacks for legacy unstamped activity', async () => {
    mockAIWorkspaceApi.getOverview.mockResolvedValueOnce({
      ...overview,
      recentConversations: {
        ...overview.recentConversations,
        items: [
          {
            ...overview.recentConversations.items[0],
            serviceId: '',
            serviceKind: '',
          },
        ],
      },
      recentRuns: {
        ...overview.recentRuns,
        items: [
          {
            ...overview.recentRuns.items[0],
            runOrigin: '',
            status: '',
            workflowName: '',
          },
        ],
      },
    });

    renderWithQueryClient(React.createElement(AIOverviewPage));

    expect(await screen.findByText(/Unknown service/)).toBeTruthy();
    expect(screen.getByText('Unknown origin')).toBeTruthy();
    expect(screen.getByText('Unknown status')).toBeTruthy();
    expect(screen.getByText('Unnamed workflow')).toBeTruthy();
  });
});
