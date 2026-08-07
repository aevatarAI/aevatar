import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import * as React from 'react';
import { history } from '@/shared/navigation/history';
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from '../../../../tests/reactQueryTestUtils';
import RunDetailPage from './RunDetailPage';

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

const mockWorkflowActivityApi = jest.requireMock(
  '@/shared/api/workflowActivityApi',
).workflowActivityApi as {
  forkRun: jest.Mock;
  getRun: jest.Mock;
  getRunGraph: jest.Mock;
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
    timeline:
      [] as import('@/shared/models/workflowActivity').WorkflowActivityTimelineEvent[],
    statistics: {
      totalSteps: 2,
      requestedSteps: 2,
      completedSteps: 2,
      roleReplyCount: 1,
      stepTypeCounts: { human_approval: 1, llm_call: 1 } as Record<
        string,
        number
      >,
    },
    usageTotals: {
      promptTokens: 4,
      completionTokens: 8,
      totalTokens: 12,
      cost: 0.02,
    },
  };
}

describe('Workflow Activity vNext run detail recovery', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockWorkflowActivityApi.getRun.mockResolvedValue(buildRunDetail());
    mockWorkflowActivityApi.getRunGraph.mockResolvedValue({
      rootNodeId: 'node-root',
      nodes: [
        { nodeId: 'node-failed', nodeType: 'step', stepId: 'step-failed' },
        { nodeId: 'node-root', nodeType: 'step', stepId: 'step-root' },
      ],
      edges: [],
    });
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

  it('confirms a retry without exposing recovery receipts in the primary interface', async () => {
    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    fireEvent.click(
      await screen.findByRole('button', { name: 'Retry failed step' }),
    );
    expect(mockWorkflowActivityApi.forkRun).not.toHaveBeenCalled();
    expect(
      screen.getByRole('dialog', { name: 'Confirm new run' }),
    ).toBeInTheDocument();
    const confirmation = screen.getByRole('dialog', {
      name: 'Confirm new run',
    });
    expect(within(confirmation).getByText('step-failed')).toBeInTheDocument();
    expect(
      within(confirmation).getByText('Investigate checkout latency'),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Confirm retry' }));

    await waitFor(() =>
      expect(mockWorkflowActivityApi.forkRun).toHaveBeenCalledWith({
        sourceRunId: 'run-source-alpha',
        startAtStepId: 'step-failed',
        input: 'Investigate checkout latency',
      }),
    );
    expect(await screen.findByText('New run started')).toBeInTheDocument();
    expect(screen.queryByText('actor-new-alpha')).not.toBeVisible();
    expect(screen.queryByText('command-alpha')).not.toBeVisible();
    expect(screen.queryByText('correlation-alpha')).not.toBeVisible();
    expect(
      screen.queryByText('/api/workflow/runs/status/command-alpha'),
    ).not.toBeVisible();
    expect(screen.getByText(/state version/i)).not.toBeVisible();
  });

  it('reports a retry failure with a toast and keeps server detail out of the page', async () => {
    mockWorkflowActivityApi.forkRun.mockRejectedValue(
      new Error('POST /api/workflow/runs/fork returned 503'),
    );

    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    fireEvent.click(
      await screen.findByRole('button', { name: 'Retry failed step' }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Confirm retry' }));

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "The new run couldn't be started",
      ),
    );
    expect(
      screen.queryByText("The new run couldn't be started"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByText('POST /api/workflow/runs/fork returned 503'),
    ).not.toBeInTheDocument();
  });

  it('keeps committed detail visible and disables run again when graph evidence fails', async () => {
    mockWorkflowActivityApi.getRunGraph.mockRejectedValue(
      new Error('graph offline'),
    );

    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    expect(await screen.findByText('Incident review')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('tab', { name: 'Execution path' }));
    expect(await screen.findByText('Human approval')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Run again' })).toBeDisabled();
  });

  it('keeps raw run and step errors behind technical details', async () => {
    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    expect(await screen.findByText('The run did not complete.')).toBeVisible();
    fireEvent.click(screen.getByRole('tab', { name: 'Steps' }));
    expect(screen.getByText('This step did not complete.')).toBeVisible();
    for (const rawError of screen.getAllByText('Approval timed out')) {
      expect(rawError).not.toBeVisible();
    }
  });

  it('shows one actionable toast for repeated GROUP_NOT_ALLOWED evidence', async () => {
    const run = buildRunDetail();
    run.finalError = 'This group cannot use the selected model.';
    run.diagnostics = [
      {
        timestampUtc: '2026-08-04T10:01:00Z',
        severity: 'error',
        code: 'GROUP_NOT_ALLOWED',
        source: 'workflow',
        message: 'This group cannot use the selected model.',
        hint: 'Choose an allowed model',
        stepId: 'step-failed',
        stepType: 'llm_call',
        targetRole: '',
      },
      {
        timestampUtc: '2026-08-04T10:01:01Z',
        severity: 'error',
        code: 'GROUP_NOT_ALLOWED',
        source: 'final_error',
        message: 'This group cannot use the selected model.',
        hint: '',
        stepId: 'step-failed',
        stepType: 'llm_call',
        targetRole: '',
      },
    ];
    run.steps[0].error = 'This group cannot use the selected model.';
    mockWorkflowActivityApi.getRun.mockResolvedValue(run);

    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledTimes(1),
    );
    const [content, options] = mockConsoleToast.error.mock.calls[0];
    expect(options).toEqual({
      duration: 8,
      key: 'run-failure:run-source-alpha:access_denied',
    });
    const toastContent = render(content).container;
    expect(
      within(toastContent).getByText(
        'This group cannot use the selected model.',
      ),
    ).toBeVisible();
    fireEvent.click(
      within(toastContent).getByRole('button', {
        name: 'Choose allowed service',
      }),
    );
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/settings',
    );
  });

  it('uses a product title instead of a raw run ID when the workflow name is missing', async () => {
    const run = buildRunDetail();
    run.summary.workflowName = '';
    mockWorkflowActivityApi.getRun.mockResolvedValue(run);

    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Run details' }),
    ).toBeInTheDocument();
    expect(screen.getByText('run-source-alpha')).not.toBeVisible();
  });

  it('names a forbidden detail response without inventing run facts', async () => {
    const { WorkflowActivityApiError } = jest.requireMock(
      '@/shared/api/workflowActivityApi',
    );
    mockWorkflowActivityApi.getRun.mockRejectedValue(
      new WorkflowActivityApiError('Access denied', 403),
    );

    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    expect(
      await screen.findByRole('heading', {
        level: 2,
        name: "You don't have access to this workspace",
      }),
    ).toBeInTheDocument();
    expect(screen.getByText('Access denied')).not.toBeVisible();
    expect(screen.queryByText('Incident review')).not.toBeInTheDocument();
  });

  it('reconciles a failed one-step outcome before technical evidence', async () => {
    const run = buildRunDetail();
    run.steps = [run.steps[0]];
    run.statistics = {
      completedSteps: 1,
      requestedSteps: 1,
      roleReplyCount: 0,
      stepTypeCounts: { human_approval: 1 },
      totalSteps: 1,
    };
    mockWorkflowActivityApi.getRun.mockResolvedValue(run);

    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    expect(
      await screen.findByText('Approval did not arrive before the deadline'),
    ).toBeVisible();
    expect(screen.getByText('Attempted')).toBeVisible();
    expect(screen.getByText('Succeeded')).toBeVisible();
    expect(
      within(screen.getByTestId('run-step-metrics')).getByText('Failed'),
    ).toBeVisible();
    expect(screen.getByText('Waiting')).toBeVisible();
    expect(screen.getByText('Skipped')).toBeVisible();
    expect(
      within(screen.getByTestId('run-step-metrics')).getByText('Not reported'),
    ).toBeVisible();
    expect(screen.queryByText('Completed steps')).not.toBeInTheDocument();

    const outcome = screen.getByTestId('run-step-metrics');
    expect(
      within(outcome).getAllByText('1', { selector: 'strong' }),
    ).toHaveLength(2);
    expect(
      within(outcome).getAllByText('0', { selector: 'strong' }),
    ).toHaveLength(2);
    const reviewFailedStep = screen.getByRole('button', {
      name: 'Review failed step',
    });
    expect(reviewFailedStep).toBeEnabled();
    fireEvent.click(reviewFailedStep);
    expect(screen.getByRole('tab', { name: 'Steps' })).toHaveAttribute(
      'aria-selected',
      'true',
    );
    for (const rawError of screen.getAllByText('Approval timed out')) {
      expect(rawError).not.toBeVisible();
    }
  });

  it('shows ordered product steps without raw IDs in the default surface', async () => {
    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    fireEvent.click(await screen.findByRole('tab', { name: 'Steps' }));

    const stepsRegion = screen.getByRole('region', { name: 'Steps' });
    expect(stepsRegion).toHaveAttribute('tabindex', '0');
    expect(within(stepsRegion).getByText('Responder · LLM call')).toBeVisible();
    expect(within(stepsRegion).getByText('Human approval')).toBeVisible();
    expect(within(stepsRegion).getByText('Succeeded')).toBeVisible();
    expect(within(stepsRegion).getByText('Failed')).toBeVisible();
    expect(
      within(stepsRegion).queryByText('step-root'),
    ).not.toBeInTheDocument();
    expect(
      within(stepsRegion).getByText(/"prompt": "Investigate"/),
    ).not.toBeVisible();
  });

  it('localizes timeline events and keeps machine vocabulary collapsed', async () => {
    const run = buildRunDetail();
    run.timeline = [
      {
        kind: 'RunStarted',
        timestampUtc: '2026-08-04T10:00:00Z',
        stage: 'runtime.start',
        message: 'command accepted',
        agentId: 'actor-internal-alpha',
        stepId: '',
        stepType: '',
        toolCall: null,
        content: '',
        data: { commandId: 'command-internal-alpha' },
      },
      {
        kind: 'InternalRoleReplyReceived',
        timestampUtc: '2026-08-04T10:00:30Z',
        stage: 'role.reply',
        message: 'responder',
        agentId: 'actor-internal-alpha',
        stepId: 'step-root',
        stepType: 'llm_call',
        toolCall: null,
        content: 'Prepared response',
        data: {},
      },
    ];
    mockWorkflowActivityApi.getRun.mockResolvedValue(run);

    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    fireEvent.click(await screen.findByRole('tab', { name: 'Timeline' }));
    expect(screen.getByText('Run started')).toBeVisible();
    expect(screen.getByText('Step produced a response')).toBeVisible();
    expect(screen.getByText('+30s')).toBeVisible();
    expect(
      screen.queryByText('InternalRoleReplyReceived'),
    ).not.toBeInTheDocument();
    expect(screen.getByText(/command-internal-alpha/)).not.toBeVisible();
  });

  it('labels reported usage honestly and does not invent a currency', async () => {
    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    fireEvent.click(await screen.findByRole('tab', { name: 'Usage' }));
    const usageRegion = screen.getByRole('tabpanel', { name: 'Usage' });
    expect(within(usageRegion).getByText('Reported')).toBeVisible();
    expect(within(usageRegion).getByText('12')).toBeVisible();
    expect(
      within(usageRegion).getByText('0.02 · Currency not reported'),
    ).toBeVisible();
    expect(within(usageRegion).getByText('Tool calls')).toBeVisible();
    expect(within(usageRegion).getByText('Not reported')).toBeVisible();
  });

  it('renders the execution path from same-version step facts, not graph IDs', async () => {
    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    fireEvent.click(await screen.findByRole('tab', { name: 'Execution path' }));
    expect(screen.getByText('Responder · LLM call')).toBeVisible();
    expect(screen.getByText('Human approval')).toBeVisible();
    expect(screen.queryByText('node-root')).not.toBeInTheDocument();
    expect(screen.queryByText('node-failed')).not.toBeInTheDocument();
  });
});
