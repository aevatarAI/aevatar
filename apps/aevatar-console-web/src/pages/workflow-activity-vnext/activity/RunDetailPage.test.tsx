import { fireEvent, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { history } from '@/shared/navigation/history';
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from '../../../../tests/reactQueryTestUtils';
import RunDetailPage from './RunDetailPage';

let mockSearch = '';

const mockConsoleToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};

jest.mock('@umijs/max', () => ({
  getIntl: () => ({
    formatMessage: (
      { defaultMessage, id }: { defaultMessage?: string; id: string },
      values?: Record<string, unknown>,
    ) =>
      (defaultMessage ?? id).replace(
        /\{(\w+)\}/g,
        (_match: string, key: string) => String(values?.[key] ?? ''),
      ),
  }),
  getLocale: () => 'en-US',
  history: {},
  setLocale: jest.fn(),
  useIntl: () => ({
    formatMessage: ({
      defaultMessage,
      id,
    }: {
      defaultMessage?: string;
      id: string;
    }) => defaultMessage ?? id,
  }),
  useModel: () => ({ initialState: { auth: { authenticated: true } } }),
}));

jest.mock('@/shared/api/workflowActivityApi', () => {
  class WorkflowActivityApiError extends Error {
    status: number;

    constructor(message: string, status: number) {
      super(message);
      this.status = status;
    }
  }

  return {
    WorkflowActivityApiError,
    workflowActivityApi: {
      forkRun: jest.fn(),
      getRun: jest.fn(),
      getRunGraph: jest.fn(),
      listRuns: jest.fn(),
    },
  };
});

jest.mock('@/shared/navigation/history', () => ({
  history: { push: jest.fn(), replace: jest.fn() },
}));

jest.mock('@/shared/ui/ConsoleHeaderActions', () => ({
  ConsoleAuthActions: () => <button type="button">Account</button>,
  ConsoleLanguageSwitch: () => <button type="button">Language</button>,
}));

jest.mock('@/shared/ui/ConsoleToast', () => ({
  useConsoleToast: () => mockConsoleToast,
}));

jest.mock('../hooks/useConsoleLocation', () => ({
  useConsoleLocation: () => ({
    hash: '',
    pathname:
      '/scopes/scope-alpha/workflow-activity-vnext/activity/run-source-alpha',
    search: mockSearch,
  }),
}));

const mockWorkflowActivityApi = jest.requireMock(
  '@/shared/api/workflowActivityApi',
).workflowActivityApi as {
  forkRun: jest.Mock;
  getRun: jest.Mock;
  getRunGraph: jest.Mock;
  listRuns: jest.Mock;
};

function buildRunDetail() {
  return {
    summary: {
      runId: 'run-source-alpha',
      workflowName: 'Incident review',
      status: 'failed',
      success: false,
      startedAtUtc: '2026-08-04T10:00:00Z',
      updatedAtUtc: '2026-08-04T10:01:00Z',
      stateVersion: 7,
      scopeId: 'scope-alpha',
      runOrigin: 'draft',
    },
    input: 'Investigate checkout latency',
    finalOutput: '',
    finalError: 'Approval timed out',
    diagnostics: [
      {
        timestampUtc: '2026-08-04T10:01:00Z',
        severity: 'error',
        code: 'APPROVAL_TIMEOUT',
        source: 'workflow',
        message: 'Approval did not arrive before the deadline',
        hint: 'Review the approval channel',
        stepId: 'step-failed',
        stepType: 'human_approval',
        targetRole: '',
      },
    ],
    steps: [
      {
        stepId: 'step-failed',
        stepType: 'human_approval',
        targetRole: '',
        requestedAtUtc: '2026-08-04T10:00:00Z',
        completedAtUtc: '2026-08-04T10:01:00Z',
        success: false,
        durationMs: 60000,
        outputPreview: '',
        error: 'Approval timed out',
        requestParameters: {},
        nextStepId: '',
        branchKey: '',
        suspensionType: 'approval',
        suspensionPrompt: 'Approve?',
        suspensionContent: '',
        suspensionTimeoutSeconds: 60,
        toolApproval: null,
        usage: {
          promptTokens: 0,
          completionTokens: 0,
          totalTokens: 0,
          cost: 0,
        },
      },
      {
        stepId: 'step-root',
        stepType: 'llm_call',
        targetRole: 'responder',
        requestedAtUtc: '2026-08-04T09:59:00Z',
        completedAtUtc: '2026-08-04T10:00:00Z',
        success: true,
        durationMs: 60000,
        outputPreview: 'Prepared response',
        error: '',
        requestParameters: { prompt: 'Investigate' },
        nextStepId: 'step-failed',
        branchKey: '',
        suspensionType: '',
        suspensionPrompt: '',
        suspensionContent: '',
        suspensionTimeoutSeconds: null,
        toolApproval: null,
        usage: {
          promptTokens: 4,
          completionTokens: 8,
          totalTokens: 12,
          cost: 0.02,
        },
      },
    ],
    timeline: [
      {
        kind: 'step_started',
        timestampUtc: '2026-08-04T09:59:00Z',
        stage: 'workflow',
        message: 'Root step started',
        agentId: 'agent-root',
        stepId: 'step-root',
        stepType: 'llm_call',
        toolCall: null,
        content: '',
        data: {},
      },
      {
        kind: 'step_finished',
        timestampUtc: '2026-08-04T10:01:00Z',
        stage: 'workflow',
        message: 'Approval step failed',
        agentId: 'agent-root',
        stepId: 'step-failed',
        stepType: 'human_approval',
        toolCall: null,
        content: '',
        data: {},
      },
    ],
    statistics: {
      totalSteps: 2,
      requestedSteps: 2,
      completedSteps: 2,
      roleReplyCount: 1,
      stepTypeCounts: { human_approval: 1, llm_call: 1 },
    },
    usageTotals: {
      promptTokens: 4,
      completionTokens: 8,
      totalTokens: 12,
      cost: 0.02,
    },
  };
}

describe('Workflow Activity vNext run detail console', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockSearch = '?definition=definition-alpha';
    mockWorkflowActivityApi.getRun.mockResolvedValue(buildRunDetail());
    mockWorkflowActivityApi.getRunGraph.mockResolvedValue({
      rootNodeId: 'node-root',
      nodes: [
        { nodeId: 'node-root', nodeType: 'step', stepId: 'step-root' },
        { nodeId: 'node-failed', nodeType: 'step', stepId: 'step-failed' },
      ],
      edges: [
        {
          edgeId: 'edge-root-failed',
          fromNodeId: 'node-root',
          toNodeId: 'node-failed',
          edgeType: 'next',
          branchKey: '',
        },
      ],
    });
    mockWorkflowActivityApi.listRuns.mockResolvedValue([
      {
        runId: 'run-source-alpha',
        workflowName: 'Incident review',
        status: 'failed',
        success: false,
        startedAtUtc: '2026-08-04T10:00:00Z',
        updatedAtUtc: '2026-08-04T10:01:00Z',
        stateVersion: 7,
        scopeId: 'scope-alpha',
        runOrigin: 'draft',
      },
      {
        runId: 'run-source-beta',
        workflowName: 'Incident review',
        status: 'completed',
        success: true,
        startedAtUtc: '2026-08-04T09:00:00Z',
        updatedAtUtc: '2026-08-04T09:01:00Z',
        stateVersion: 6,
        scopeId: 'scope-alpha',
        runOrigin: 'draft',
      },
      {
        runId: 'run-other',
        workflowName: 'Other workflow',
        status: 'completed',
        success: true,
        startedAtUtc: '2026-08-04T08:00:00Z',
        updatedAtUtc: '2026-08-04T08:01:00Z',
        stateVersion: 5,
        scopeId: 'scope-alpha',
        runOrigin: 'draft',
      },
    ]);
    mockWorkflowActivityApi.forkRun.mockResolvedValue({
      accepted: true,
      sourceRunId: 'run-source-alpha',
      newRunActorId: 'actor-new-alpha',
      workflowName: 'Incident review',
      acceptedCommandId: 'command-alpha',
      correlationId: 'correlation-alpha',
      statusUrl: '/api/workflow/runs/status/command-alpha',
    });
  });

  afterEach(() => cleanupTestQueryClients());

  it('renders a published-runs style console for the current run and preserves workflow context when switching history', async () => {
    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    await waitFor(() =>
      expect(mockWorkflowActivityApi.listRuns).toHaveBeenCalledWith(
        'scope-alpha',
        {
          definitionActorIds: ['definition-alpha'],
          take: 100,
        },
      ),
    );

    expect(
      await screen.findByRole('heading', { name: 'Published runs' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Incident review' }),
    ).toBeInTheDocument();
    expect(screen.getByText('Logs')).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Output' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Input' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Timeline' })).toBeInTheDocument();
    expect(
      screen.queryByRole('tab', { name: 'Diagnostics' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('tab', { name: 'Statistics and usage' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('tab', { name: 'Graph' }),
    ).not.toBeInTheDocument();

    const selectedRun = screen.getByRole('button', {
      name: 'Open run-source-alpha',
    });
    expect(selectedRun).toHaveAttribute('aria-current', 'true');

    expect(
      screen.queryByRole('button', { name: 'Open run-other' }),
    ).not.toBeInTheDocument();

    fireEvent.click(
      screen.getByRole('button', {
        name: 'Open run-source-beta',
      }),
    );

    expect(history.push).toHaveBeenLastCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity/run-source-beta?definition=definition-alpha',
    );
  });
});
