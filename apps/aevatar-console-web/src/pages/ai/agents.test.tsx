import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import React from 'react';
import { persistAuthSession } from '@/shared/auth/session';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import AIAgentsPage from './agents';

jest.mock('@/shared/api/aiWorkspaceApi', () => {
  const actual = jest.requireActual('@/shared/api/aiWorkspaceApi');
  return {
    ...actual,
    aiWorkspaceApi: {
      getAgents: jest.fn(),
      getContext: jest.fn(),
    },
  };
});

const { aiWorkspaceApi: mockAIWorkspaceApi } = jest.requireMock(
  '@/shared/api/aiWorkspaceApi',
) as {
  aiWorkspaceApi: {
    getAgents: jest.Mock;
    getContext: jest.Mock;
  };
};

const context = {
  scopeId: 'scope-alpha',
  consistency: 'independent_read_models',
  pages: {
    overview: '/ai',
    chat: '/ai/chat',
    agents: '/ai/agents',
  },
  apis: {
    overview: '/api/ai/overview',
    chat: '/api/chat',
    agents: '/api/ai/agents',
    ownedAgentProfiles: '/api/scopes/scope-alpha/agent-profiles',
    systemAgentProfiles: '/api/agent-profiles/system',
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
  },
};

const agents = {
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
    totalCount: 7,
    authorityStateVersion: 17,
    updatedAtUtc: '2026-08-18T08:00:00Z',
    error: null,
  },
  systemTemplates: {
    source: 'agent_profile_catalog',
    ownerKind: 'system',
    scopeId: null,
    availability: 'available',
    items: [
      {
        profileId: 'profile-system',
        profileSlug: 'research',
        displayName: 'Research Assistant',
        purpose: 'Collect cited evidence.',
        publishedRevision: 8,
        publishedSnapshotSha256: 'def456',
        published: true,
        status: 'active',
      },
    ],
    nextCursor: null,
    totalCount: 1,
    authorityStateVersion: 29,
    updatedAtUtc: '2026-08-18T08:05:00Z',
    error: null,
  },
};

describe('AIAgentsPage', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    setLocale('en-US', false);
    window.history.replaceState({}, '', '/ai/agents');
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
    mockAIWorkspaceApi.getAgents.mockResolvedValue(agents);
  });

  afterEach(() => {
    jest.restoreAllMocks();
    window.localStorage.clear();
  });

  it('keeps owned Agent Profiles separate from read-only system templates', async () => {
    renderWithQueryClient(React.createElement(AIAgentsPage));

    expect(await screen.findByRole('heading', { name: 'Agents' })).toBeTruthy();
    const ownedSection = screen.getByRole('region', { name: 'My Agents' });
    expect(within(ownedSection).getByText('7')).toBeTruthy();
    expect(screen.getByText('Writer')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'draft-agent' })).toBeTruthy();
    expect(screen.getByText('Not published')).toBeTruthy();
    expect(
      screen.getByRole('heading', { name: 'System Templates' }),
    ).toBeTruthy();
    expect(screen.getByText('Research Assistant')).toBeTruthy();
    expect(screen.getAllByText('Read only').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Scope scope-alpha').length).toBeGreaterThan(0);
    expect(screen.getByText('owner@example.com')).toBeTruthy();
    const navigation = screen.getByRole('navigation', {
      name: 'AI workspace navigation',
    });
    expect(within(navigation).getByText('Overview')).toBeTruthy();
    expect(within(navigation).getByText('Chat')).toBeTruthy();
    expect(within(navigation).getByText('Agents')).toBeTruthy();
    expect(within(navigation).queryByText('Models')).toBeNull();
  });

  it('shows one unavailable source without hiding the healthy source', async () => {
    mockAIWorkspaceApi.getAgents.mockResolvedValueOnce({
      ...agents,
      owned: {
        ...agents.owned,
        availability: 'unavailable',
        items: [],
        nextCursor: null,
        authorityStateVersion: null,
        updatedAtUtc: null,
        error: {
          code: 'OWNED_AGENT_PROFILES_UNAVAILABLE',
          message: 'Owned Agent Profiles are temporarily unavailable.',
        },
      },
    });

    renderWithQueryClient(React.createElement(AIAgentsPage));

    expect(
      await screen.findByText(
        'Owned Agent Profiles are temporarily unavailable.',
      ),
    ).toBeTruthy();
    expect(screen.getByText('Research Assistant')).toBeTruthy();
  });

  it('shows Models only when its page, API, and feature agree', async () => {
    mockAIWorkspaceApi.getContext.mockResolvedValueOnce({
      ...context,
      pages: { ...context.pages, models: '/ai/models' },
      apis: { ...context.apis, models: '/api/ai/models' },
      features: {
        ...context.features,
        models: {
          availability: 'available',
          page: '/ai/models',
          api: '/api/ai/models',
        },
      },
    });

    renderWithQueryClient(React.createElement(AIAgentsPage));

    const navigation = await screen.findByRole('navigation', {
      name: 'AI workspace navigation',
    });
    expect(await within(navigation).findByText('Models')).toBeTruthy();
  });

  it('does not query Agents when the backend omits the capability contract', async () => {
    mockAIWorkspaceApi.getContext.mockResolvedValueOnce({
      ...context,
      apis: {
        ...context.apis,
        agents: undefined,
      },
    });

    renderWithQueryClient(React.createElement(AIAgentsPage));

    expect(await screen.findByText('Agents not available')).toBeTruthy();
    expect(mockAIWorkspaceApi.getAgents).not.toHaveBeenCalled();
  });

  it('passes only opaque collection cursors when paging', async () => {
    mockAIWorkspaceApi.getAgents
      .mockResolvedValueOnce(agents)
      .mockResolvedValueOnce({
        ...agents,
        owned: {
          ...agents.owned,
          items: [],
          nextCursor: null,
        },
      });

    renderWithQueryClient(React.createElement(AIAgentsPage));

    fireEvent.click(await screen.findByRole('button', { name: 'Next page' }));

    await waitFor(() => {
      expect(mockAIWorkspaceApi.getAgents).toHaveBeenLastCalledWith(
        {
          ownedCursor: 'owned-next',
          systemCursor: undefined,
          take: 24,
        },
        expect.any(AbortSignal),
      );
    });
    expect(
      mockAIWorkspaceApi.getAgents.mock.calls.at(-1)?.[0],
    ).not.toHaveProperty('scopeId');
  });
});
