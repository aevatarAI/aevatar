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
    timeline: [],
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
    recoveryCapability: {
      retryFailedStep: {
        eligibility: 1,
        unavailableReasonCode: 0,
        unavailableReason: '',
        recommendedActions: [1],
        startingStepId: 'step-failed',
        reusesPriorStepOutputs: true,
        mayIncurModelOrToolCost: false,
      },
      runAgain: {
        eligibility: 1,
        unavailableReasonCode: 0,
        unavailableReason: '',
        recommendedActions: [2],
        startingStepId: 'step-root',
        reusesPriorStepOutputs: false,
        mayIncurModelOrToolCost: true,
      },
      workflowDefinitionRevisionId: 'revision-alpha',
      workflowDefinitionVersion: 12,
    },
    lineage: {
      availability: 1,
      unavailableReason: '',
      retryFork: {
        availability: 1,
        sourceRunId: 'run-source-alpha',
        originalRunId: 'run-original-alpha',
        attempt: 2,
        startAtStepId: 'step-failed',
        childRuns: [
          {
            runId: 'run-retry-child',
            actorId: 'actor-retry-child',
            relationshipId: 'relationship-retry-child',
            stepId: 'step-failed',
            attempt: 3,
            relationKind: 1,
          },
        ],
      },
      subWorkflow: {
        availability: 1,
        parentRunId: 'run-parent-alpha',
        parentActorId: 'actor-parent-alpha',
        parentStepId: 'step-parent',
        rootRunId: 'run-root-alpha',
        depth: 1,
        childRuns: [
          {
            runId: 'run-sub-child',
            actorId: 'actor-sub-child',
            relationshipId: 'relationship-sub-child',
            stepId: 'step-child',
            attempt: 1,
            relationKind: 2,
          },
        ],
      },
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
      newRunId: 'run-new-alpha',
      newRunActorId: 'actor-new-alpha',
      workflowName: 'Incident review',
      acceptedCommandId: 'command-alpha',
      correlationId: 'correlation-alpha',
      statusUrl: '/api/workflow/runs/status/command-alpha',
    });
  });

  afterEach(() => cleanupTestQueryClients());

  it('confirms an authoritative retry and opens the accepted run by public run ID', async () => {
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
    expect(
      within(confirmation).getByText('revision-alpha'),
    ).toBeInTheDocument();
    expect(within(confirmation).getByText('12')).toBeInTheDocument();
    expect(
      within(confirmation).getByText('Prior step outputs will be reused.'),
    ).toBeInTheDocument();
    expect(
      within(confirmation).getByText(
        "This creates a separate run. The source run won't change.",
      ),
    ).toBeInTheDocument();
    expect(confirmation.querySelector('.ant-alert-warning')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Confirm retry' }));

    await waitFor(() =>
      expect(mockWorkflowActivityApi.forkRun).toHaveBeenCalledWith({
        sourceRunId: 'run-source-alpha',
        startAtStepId: 'step-failed',
        input: 'Investigate checkout latency',
      }),
    );
    expect(await screen.findByText('New run accepted')).toBeInTheDocument();
    const openNewRun = screen.getByRole('button', { name: 'Open new run' });
    fireEvent.click(openNewRun);
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity/run-new-alpha',
    );
    expect(history.push).not.toHaveBeenCalledWith(
      expect.stringContaining('actor-new-alpha'),
    );

    const actorAddress = screen.getByText('actor-new-alpha');
    expect(actorAddress).not.toBeVisible();
    const receiptDetails = actorAddress.closest('details');
    expect(receiptDetails).not.toBeNull();
    fireEvent.click(
      within(receiptDetails as HTMLElement).getByText('Technical details'),
    );
    expect(screen.getByText('actor-new-alpha')).toBeVisible();
    expect(screen.getByText('command-alpha')).toBeVisible();
    expect(screen.getByText('correlation-alpha')).toBeVisible();
    expect(
      screen.getByText('/api/workflow/runs/status/command-alpha'),
    ).toBeVisible();
  });

  it('shows the cost and non-reuse semantics for an authoritative run again', async () => {
    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Run again' }));
    const confirmation = screen.getByRole('dialog', {
      name: 'Confirm new run',
    });
    expect(within(confirmation).getByText('step-root')).toBeInTheDocument();
    expect(
      within(confirmation).getByText('Prior step outputs will not be reused.'),
    ).toBeInTheDocument();
    expect(
      within(confirmation).getByText(
        'This action may incur model or tool costs again.',
      ),
    ).toBeInTheDocument();
  });

  it('keeps a backend-unavailable action focusable and shows its reason', async () => {
    const run = buildRunDetail();
    run.recoveryCapability.runAgain = {
      eligibility: 3,
      unavailableReasonCode: 3,
      unavailableReason: 'Restore model access before starting another run.',
      recommendedActions: [3, 7],
      startingStepId: '',
      reusesPriorStepOutputs: false,
      mayIncurModelOrToolCost: false,
    };
    mockWorkflowActivityApi.getRun.mockResolvedValue(run);

    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    const runAgain = await screen.findByRole('button', { name: 'Run again' });
    expect(runAgain).toHaveAttribute('aria-disabled', 'true');
    expect(runAgain).not.toBeDisabled();
    runAgain.focus();
    expect(runAgain).toHaveFocus();
    fireEvent.click(runAgain);
    expect(
      screen.queryByRole('dialog', { name: 'Confirm new run' }),
    ).toBeNull();
    expect(
      screen.getByText('Restore model access before starting another run.'),
    ).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Review settings' }));
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/settings',
    );
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

  it('keeps capability-eligible recovery enabled when graph loading fails', async () => {
    mockWorkflowActivityApi.getRunGraph.mockRejectedValue(
      new Error('graph offline'),
    );

    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    expect(await screen.findByText('Incident review')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('tab', { name: 'Graph' }));
    expect(
      await screen.findByText('Run graph unavailable'),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Run again' })).toHaveAttribute(
      'aria-disabled',
      'false',
    );
  });

  it('renders retry history and sub-workflows as separate public-run link groups', async () => {
    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    expect(
      await screen.findByRole('heading', { name: 'Related runs' }),
    ).toBeVisible();
    expect(
      screen.getByRole('heading', { name: 'Retry history' }),
    ).toBeVisible();
    expect(
      screen.getByRole('heading', { name: 'Sub-workflows' }),
    ).toBeVisible();

    for (const runId of [
      'run-source-alpha',
      'run-original-alpha',
      'run-retry-child',
      'run-parent-alpha',
      'run-root-alpha',
      'run-sub-child',
    ]) {
      expect(screen.getByRole('link', { name: runId })).toHaveAttribute(
        'href',
        `/scopes/scope-alpha/workflow-activity-vnext/activity/${runId}`,
      );
    }
    expect(screen.queryByText('actor-retry-child')).not.toBeInTheDocument();
    expect(screen.queryByText('actor-parent-alpha')).not.toBeInTheDocument();
    expect(screen.queryByText('actor-sub-child')).not.toBeInTheDocument();
  });

  it('keeps run detail usable when lineage is unavailable', async () => {
    const run = buildRunDetail();
    run.lineage.availability = 3;
    run.lineage.unavailableReason =
      'Lineage has not been projected for this run.';
    mockWorkflowActivityApi.getRun.mockResolvedValue(run);

    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    expect(await screen.findByText('Incident review')).toBeVisible();
    expect(
      screen.getByText('Lineage has not been projected for this run.'),
    ).toBeVisible();
  });

  it('keeps raw run and step errors behind technical details', async () => {
    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    expect(await screen.findByText('The run did not complete.')).toBeVisible();
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
    expect(
      screen.queryByRole('heading', { name: 'run-source-alpha' }),
    ).not.toBeInTheDocument();
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

  it('renders returned diagnostics, request parameters, statistics, and usage facts', async () => {
    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    expect(
      await screen.findByText(/"prompt": "Investigate"/),
    ).toBeInTheDocument();

    const stepsRegion = screen.getByRole('region', { name: 'Steps' });
    expect(stepsRegion).toHaveAttribute('tabindex', '0');
    expect(
      within(stepsRegion).getByText('step-root').closest('td'),
    ).toHaveAttribute('data-label', 'Step');

    fireEvent.click(screen.getByRole('tab', { name: 'Diagnostics' }));
    const diagnosticsRegion = screen.getByRole('region', {
      name: 'Diagnostics',
    });
    expect(diagnosticsRegion).toHaveAttribute('tabindex', '0');
    expect(
      within(diagnosticsRegion).getByText('APPROVAL_TIMEOUT').closest('td'),
    ).toHaveAttribute('data-label', 'Code');
    expect(
      screen.getByText('Approval did not arrive before the deadline'),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: 'Statistics and usage' }));
    expect(screen.getByText('12')).toBeInTheDocument();
    expect(screen.getByText('0.02')).toBeInTheDocument();
  });
});
