import { fireEvent, screen } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import React from 'react';
import { AIWorkspaceApiError } from '@/shared/api/aiWorkspaceApi';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import AIRunDetailPage from './run-detail';

const workspaceContext = {
  context: {
    scopeId: 'scope-alpha',
    consistency: 'independent_read_models',
    pages: { activity: '/ai/activity' },
    apis: {
      activity: '/api/ai/activity',
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
      getRun: jest.fn(),
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
  aiWorkspaceApi: { getRun: jest.Mock };
};

const usage = {
  promptTokens: 120,
  completionTokens: 40,
  totalTokens: 160,
  cost: 0.03,
};

const detail = {
  source: 'workflow_run_observatory',
  scopeId: 'scope-alpha',
  authorityStateVersion: 13,
  updatedAtUtc: '2026-08-18T08:20:00Z',
  reportVersion: 'report-7',
  sections: {
    overview: {
      detailStateVersion: 13,
      sourceStateVersion: 13,
      versionStatus: 'aligned',
      reason: null,
    },
    steps: {
      detailStateVersion: 13,
      sourceStateVersion: 12,
      versionStatus: 'version_mismatch',
      reason: 'Step details are stale.',
    },
    timeline: {
      detailStateVersion: 13,
      sourceStateVersion: 13,
      versionStatus: 'aligned',
      reason: null,
    },
    executionPath: {
      detailStateVersion: 13,
      sourceStateVersion: 0,
      versionStatus: 'unavailable',
      reason: 'Execution path is unavailable.',
    },
  },
  summary: {
    runId: 'run.alpha',
    workflowId: 'wf-alpha',
    workflowName: 'Release workflow',
    status: 'failed',
    runOrigin: 'chat',
    success: false,
    inputSummary: 'Prepare release notes',
    currentStep: null,
    firstFailure: {
      stepId: 'step-alpha',
      message: 'Release validation failed.',
      availability: 'available',
    },
    waiting: null,
    startedAtUtc: '2026-08-18T08:00:00Z',
    completedAtUtc: '2026-08-18T08:05:00Z',
    updatedAtUtc: '2026-08-18T08:20:00Z',
    durationMs: 300000,
    authorityStateVersion: 13,
  },
  finalOutput: 'Sanitized output',
  steps: [],
  timeline: [
    {
      kind: 'step_completed',
      timestampUtc: '2026-08-18T08:05:00Z',
      stage: 'completed',
      stepId: 'step-alpha',
      toolCall: null,
    },
  ],
  operations: [],
  statistics: {
    totalSteps: 1,
    requestedSteps: 1,
    completedSteps: 1,
    roleReplyCount: 0,
    stepTypeCounts: { role: 1 },
  },
  usageTotals: usage,
  rawPayload: 'must-not-render',
};

describe('AIRunDetailPage', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    setLocale('en-US', false);
    window.history.replaceState({}, '', '/ai/activity/runs/run.alpha');
    mockAIWorkspaceApi.getRun.mockResolvedValue(detail);
  });

  it('renders result-first sanitized detail with explicit partial freshness', async () => {
    renderWithQueryClient(React.createElement(AIRunDetailPage));

    expect(
      await screen.findByRole('heading', { name: 'Release workflow' }),
    ).toBeInTheDocument();
    expect(screen.getByText('Release validation failed.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('tab', { name: 'Freshness' }));
    expect(screen.getByText('Step details are stale.')).toBeInTheDocument();
    expect(
      screen.getByText('Execution path is unavailable.'),
    ).toBeInTheDocument();
    expect(screen.getByText('Version mismatch')).toBeInTheDocument();
    expect(screen.queryByText('must-not-render')).toBeNull();
    expect(mockAIWorkspaceApi.getRun).toHaveBeenCalledWith(
      'run.alpha',
      expect.any(AbortSignal),
    );
  });

  it('distinguishes a scoped not-found response from a retryable source error', async () => {
    mockAIWorkspaceApi.getRun.mockRejectedValueOnce(
      new AIWorkspaceApiError(
        'Workflow run was not found.',
        404,
        'WORKFLOW_RUN_NOT_FOUND',
      ),
    );

    renderWithQueryClient(React.createElement(AIRunDetailPage));

    expect(await screen.findByText('Run not found')).toBeInTheDocument();
    expect(screen.getByText('Back to Activity')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });
});
