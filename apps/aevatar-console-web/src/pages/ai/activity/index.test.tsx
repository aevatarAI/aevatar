import { screen } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import React from 'react';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import AIActivityPage from './index';

const workspaceContext = {
  context: {
    scopeId: 'scope-alpha',
    consistency: 'independent_read_models',
    pages: { activity: '/ai/activity' },
    apis: {
      activity: '/api/ai/activity',
      conversations: '/api/ai/activity/conversations',
      runs: '/api/ai/activity/runs',
    },
    features: {
      activity: {
        availability: 'available',
        page: '/ai/activity',
        api: '/api/ai/activity',
      },
    },
  },
  queryAuthority: {
    principalId: 'user-alpha',
    sessionExpiresAt: 1_800_000_000_000,
  },
  scopeId: 'scope-alpha',
};

jest.mock('@/shared/api/aiWorkspaceApi', () => {
  const actual = jest.requireActual('@/shared/api/aiWorkspaceApi');
  return {
    ...actual,
    aiWorkspaceApi: {
      getConversations: jest.fn(),
      getRuns: jest.fn(),
    },
  };
});

jest.mock('../components/AIWorkspaceShell', () => {
  const mockReact = jest.requireActual('react');
  return {
    __esModule: true,
    default: ({ children }: { children: never }) =>
      mockReact.createElement(mockReact.Fragment, null, children),
    useAIWorkspaceContext: () => workspaceContext,
  };
});

const { aiWorkspaceApi: mockAIWorkspaceApi } = jest.requireMock(
  '@/shared/api/aiWorkspaceApi',
) as {
  aiWorkspaceApi: {
    getConversations: jest.Mock;
    getRuns: jest.Mock;
  };
};

const healthyRuns = {
  source: 'workflow_run_observatory',
  scopeId: 'scope-alpha',
  availability: 'available',
  items: [
    {
      runId: 'run.alpha',
      workflowId: 'wf-alpha',
      workflowName: 'Release workflow',
      status: 'completed',
      runOrigin: 'chat',
      success: true,
      inputSummary: 'Prepare release notes',
      currentStep: null,
      firstFailure: null,
      waiting: null,
      startedAtUtc: '2026-08-18T08:00:00Z',
      completedAtUtc: '2026-08-18T08:01:00Z',
      updatedAtUtc: '2026-08-18T08:01:00Z',
      durationMs: 60000,
      authorityStateVersion: 7,
    },
  ],
  nextCursor: null,
  hasMore: false,
  totalCount: 1,
  error: null,
};

describe('AIActivityPage', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    setLocale('en-US', false);
    window.history.replaceState({}, '', '/ai/activity');
    mockAIWorkspaceApi.getConversations.mockResolvedValue({
      source: 'chat_history',
      scopeId: 'scope-alpha',
      availability: 'unavailable',
      items: [],
      nextCursor: null,
      error: {
        code: 'CONVERSATIONS_UNAVAILABLE',
        message: 'Conversation activity is temporarily unavailable.',
      },
    });
    mockAIWorkspaceApi.getRuns.mockResolvedValue(healthyRuns);
  });

  it('keeps a healthy run source visible when conversations are unavailable', async () => {
    renderWithQueryClient(React.createElement(AIActivityPage));

    expect(await screen.findByText('Release workflow')).toBeInTheDocument();
    expect(
      screen.getByText('Conversation activity is temporarily unavailable.'),
    ).toBeInTheDocument();
    expect(screen.getByText('Release workflow').closest('a')).toHaveAttribute(
      'href',
      '/ai/activity/runs/run.alpha',
    );
    expect(mockAIWorkspaceApi.getConversations).toHaveBeenCalledWith(
      { cursor: undefined, take: 20 },
      expect.any(AbortSignal),
    );
    expect(mockAIWorkspaceApi.getRuns).toHaveBeenCalledWith(
      { cursor: undefined, q: undefined, status: undefined, take: 20 },
      expect.any(AbortSignal),
    );
  });

  it('does not render activity rows returned under another scope', async () => {
    mockAIWorkspaceApi.getRuns.mockResolvedValueOnce({
      ...healthyRuns,
      scopeId: 'scope-other',
      items: [{ ...healthyRuns.items[0], workflowName: 'Hidden foreign run' }],
    });

    renderWithQueryClient(React.createElement(AIActivityPage));

    expect(
      await screen.findByText('Activity scope mismatch'),
    ).toBeInTheDocument();
    expect(screen.queryByText('Hidden foreign run')).toBeNull();
  });
});
