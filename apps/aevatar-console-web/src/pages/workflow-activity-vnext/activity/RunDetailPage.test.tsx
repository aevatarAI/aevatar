import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { history } from '@/shared/navigation/history';
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from '../../../../tests/reactQueryTestUtils';
import RunDetailPage from './RunDetailPage';

type WorkflowActivityRunDetailFixture =
  import('@/shared/models/workflowActivity').WorkflowActivityRunDetail;
type WorkflowActivityRunFeedRowFixture =
  import('@/shared/models/workflowActivity').WorkflowActivityRunFeedRow;
type WorkflowActivityRunGraphFixture =
  import('@/shared/models/workflowActivity').WorkflowActivityRunGraph;
type WorkflowRunLineageFixture =
  import('@/shared/models/workflowActivity').WorkflowRunLineage;
type WorkflowRunRecoveryCapabilityFixture =
  import('@/shared/models/workflowActivity').WorkflowRunRecoveryCapability;

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
      listActivityRuns: jest.fn(),
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
  listActivityRuns: jest.Mock;
};

function createDeferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, reject, resolve };
}

function buildRecoveryCapability(): WorkflowRunRecoveryCapabilityFixture {
  return {
    retryFailedStep: {
      eligibility: 1,
      unavailableReasonCode: 0,
      unavailableReason: '',
      recommendedActions: [1],
      startingStepId: 'step-failed',
      reusesPriorStepOutputs: true,
      mayIncurModelOrToolCost: true,
    },
    runAgain: {
      eligibility: 1,
      unavailableReasonCode: 0,
      unavailableReason: '',
      recommendedActions: [1],
      startingStepId: 'step-root',
      reusesPriorStepOutputs: false,
      mayIncurModelOrToolCost: true,
    },
    workflowDefinitionRevisionId: 'revision-alpha',
    workflowDefinitionVersion: 3,
  };
}

function buildLineage(): WorkflowRunLineageFixture {
  return {
    availability: 0,
    retryFork: {
      availability: 0,
      sourceRunId: '',
      originalRunId: '',
      attempt: 0,
      startAtStepId: '',
      childRuns: [],
    },
    subWorkflow: {
      availability: 0,
      parentRunId: '',
      parentActorId: '',
      parentStepId: '',
      rootRunId: '',
      depth: 0,
      childRuns: [],
    },
    unavailableReason: '',
  };
}

function buildRunDetail(): WorkflowActivityRunDetailFixture {
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
    recoveryCapability: buildRecoveryCapability(),
    lineage: buildLineage(),
  };
}

function buildActivityRow(
  overrides: Partial<{
    runId: string;
    workflowId: string;
    workflowName: string;
    status: string;
    runOrigin: string;
    success: boolean | null;
    startedAtUtc: string | null;
    updatedAtUtc: string;
  }> = {},
): WorkflowActivityRunFeedRowFixture {
  const detail = buildRunDetail();
  return {
    runId: overrides.runId ?? 'run-source-alpha',
    actorId: 'actor-technical-alpha',
    workflowId: overrides.workflowId ?? 'wf-alpha',
    workflowName: overrides.workflowName ?? 'Incident review',
    scopeId: 'scope-alpha',
    status: overrides.status ?? 'failed',
    runOrigin: overrides.runOrigin ?? 'draft',
    success: overrides.success ?? false,
    initiator: {
      platform: 'nyxid',
      tenant: 'tenant-alpha',
      externalUserId: 'user-alpha',
      scope: 'scope-alpha',
      bindingId: 'binding-alpha',
      displayValue: 'Abigail',
      availability: 'available',
    },
    inputSummary: 'Investigate checkout latency',
    currentStep: {
      stepId: 'step-failed',
      inputSummary: 'Connector request',
      availability: 'available',
    },
    firstFailure: {
      stepId: 'step-failed',
      message: 'Approval timed out',
      availability: 'available',
    },
    waiting: {
      stepId: '',
      waitingKind: '',
      prompt: '',
      availability: 'unavailable',
    },
    startedAtUtc: overrides.startedAtUtc ?? '2026-08-04T10:00:00Z',
    completedAtUtc: null,
    updatedAtUtc: overrides.updatedAtUtc ?? '2026-08-04T10:01:00Z',
    durationMs: 60000,
    stateVersion: 7,
    recoveryCapability: detail.recoveryCapability,
    lineage: detail.lineage,
  };
}

function RunDetailRouteHarness() {
  const [selectedRunId, setSelectedRunId] = React.useState('run-source-alpha');

  return (
    <>
      <button
        aria-label="Route to run-source-beta"
        onClick={() => setSelectedRunId('run-source-beta')}
        type="button"
      />
      <RunDetailPage runId={selectedRunId} scopeId="scope-alpha" />
    </>
  );
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
    mockWorkflowActivityApi.listActivityRuns.mockResolvedValue({
      items: [],
      nextCursor: null,
      hasMore: false,
      totalCount: 0,
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

  it('keeps the Run detail workspace stable while authoritative data loads', () => {
    const pending = new Promise<never>(() => undefined);
    mockWorkflowActivityApi.getRun.mockReturnValue(pending);
    mockWorkflowActivityApi.getRunGraph.mockReturnValue(pending);
    mockWorkflowActivityApi.listRuns.mockReturnValue(pending);

    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    expect(
      screen.getByRole('heading', { name: 'Run details' }),
    ).toBeInTheDocument();

    const status = screen.getByRole('status');
    expect(status).toHaveAttribute('aria-busy', 'true');
    expect(status).toHaveClass(
      'wa-vnext-run-detail',
      'wa-vnext-run-detail--bounded',
      'wa-vnext-run-detail--loading',
    );
    expect(screen.getByText('Loading run details…')).toHaveClass(
      'aevatar-loading-visually-hidden',
    );

    expect(
      document.querySelector('.wa-vnext-run-detail__rail'),
    ).toBeInTheDocument();
    expect(
      document.querySelector('.wa-vnext-run-detail__stage'),
    ).toBeInTheDocument();
    expect(
      document.querySelector('.wa-vnext-run-detail__graph'),
    ).toBeInTheDocument();
    expect(
      document.querySelector('.wa-vnext-run-detail__details'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Back to Activity' }),
    ).toBeEnabled();

    expect(
      screen.queryByRole('heading', { name: 'Loading run…' }),
    ).not.toBeInTheDocument();
    expect(document.querySelector('.wa-vnext__state')).not.toBeInTheDocument();
  });

  it('keeps committed Run history usable while the selected Run detail loads', async () => {
    renderWithQueryClient(<RunDetailRouteHarness />);

    expect(
      await screen.findByRole('heading', { name: 'Published runs' }),
    ).toBeInTheDocument();
    const alphaRun = screen.getByRole('button', {
      name: 'Open run-source-alpha',
    });
    expect(
      screen.getByRole('button', { name: 'Open run-source-beta' }),
    ).toBeInTheDocument();
    expect(alphaRun).toHaveAttribute('aria-current', 'true');
    const historyRail = document.querySelector(
      '.wa-vnext-run-detail__rail-list',
    );
    expect(historyRail).toBeInstanceOf(HTMLElement);
    (historyRail as HTMLElement).scrollTop = 128;

    const pendingDetail = createDeferred<WorkflowActivityRunDetailFixture>();
    const pendingGraph = createDeferred<WorkflowActivityRunGraphFixture>();
    mockWorkflowActivityApi.getRun.mockReturnValueOnce(pendingDetail.promise);
    mockWorkflowActivityApi.getRunGraph.mockReturnValueOnce(
      pendingGraph.promise,
    );

    fireEvent.click(
      screen.getByRole('button', { name: 'Route to run-source-beta' }),
    );

    expect(
      screen.getByRole('heading', { name: 'Published runs' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Open run-source-alpha' }),
    ).not.toHaveAttribute('aria-current');
    expect(
      screen.getByRole('button', { name: 'Open run-source-beta' }),
    ).toHaveAttribute('aria-current', 'true');
    expect(screen.getByText('Loading run details…')).toBeInTheDocument();
    expect(screen.queryByText('Loading run history…')).not.toBeInTheDocument();
    expect(mockWorkflowActivityApi.listRuns).toHaveBeenCalledTimes(1);
    expect(document.querySelector('.wa-vnext-run-detail__rail-list')).toBe(
      historyRail,
    );
    expect((historyRail as HTMLElement).scrollTop).toBe(128);

    const stage = document.querySelector('.wa-vnext-run-detail__stage');
    expect(stage).toHaveAttribute('role', 'status');
    expect(stage).toHaveAttribute('aria-busy', 'true');
    expect(document.querySelector('.wa-vnext-run-detail')).not.toHaveAttribute(
      'aria-busy',
    );

    const loadedBetaRunFixture = buildRunDetail();
    const loadedBetaRun = {
      ...loadedBetaRunFixture,
      summary: {
        ...loadedBetaRunFixture.summary,
        runId: 'run-source-beta',
        status: 'completed',
        success: true,
        updatedAtUtc: '2026-08-04T09:01:00Z',
      },
    };

    await act(async () => {
      pendingDetail.resolve(loadedBetaRun);
      pendingGraph.resolve({
        rootNodeId: 'node-root',
        nodes: [{ nodeId: 'node-root', nodeType: 'step', stepId: 'step-root' }],
        edges: [],
      });
      await Promise.all([pendingDetail.promise, pendingGraph.promise]);
    });

    expect(
      await screen.findByRole('heading', { name: 'Incident review' }),
    ).toBeInTheDocument();
    expect(screen.queryByText('Loading run details…')).not.toBeInTheDocument();
  });

  it('acknowledges a grouped refresh and confirms when every source succeeds', async () => {
    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    const refreshButton = await screen.findByRole('button', {
      name: 'Refresh',
    });
    const detailRefresh = createDeferred<WorkflowActivityRunDetailFixture>();
    const graphRefresh = createDeferred<WorkflowActivityRunGraphFixture>();
    const historyRefresh =
      createDeferred<WorkflowActivityRunDetailFixture['summary'][]>();
    mockWorkflowActivityApi.getRun.mockReturnValueOnce(detailRefresh.promise);
    mockWorkflowActivityApi.getRunGraph.mockReturnValueOnce(
      graphRefresh.promise,
    );
    mockWorkflowActivityApi.listRuns.mockReturnValueOnce(
      historyRefresh.promise,
    );

    fireEvent.click(refreshButton);

    const refreshingLabel = screen.queryByText('Refreshing…');
    expect(refreshingLabel).toBeInTheDocument();
    const refreshingButton = refreshingLabel?.closest('button');
    expect(refreshingButton).toBeInstanceOf(HTMLButtonElement);
    expect(refreshingButton).toBeDisabled();
    const refreshStatus = document.querySelector('.aevatar-loading-overlay');
    expect(refreshStatus).toHaveAttribute('role', 'status');
    expect(refreshStatus).toHaveAttribute(
      'aria-label',
      'Refreshing run details…',
    );
    expect(refreshStatus).toHaveClass('aevatar-loading-overlay');
    expect(
      refreshStatus?.querySelectorAll('.aevatar-loading-dot'),
    ).toHaveLength(3);
    expect(
      refreshStatus?.querySelector('.wa-vnext-run-detail__refresh-indicator'),
    ).not.toBeInTheDocument();
    expect(document.querySelector('.wa-vnext-run-detail')).toHaveAttribute(
      'aria-busy',
      'true',
    );
    expect(
      document.querySelector('.wa-vnext-run-detail__refresh-content'),
    ).toHaveAttribute('inert');
    expect(
      screen.getByRole('heading', { name: 'Incident review' }),
    ).toBeInTheDocument();
    expect(mockWorkflowActivityApi.getRun).toHaveBeenCalledTimes(2);
    expect(mockWorkflowActivityApi.getRunGraph).toHaveBeenCalledTimes(2);
    expect(mockWorkflowActivityApi.listRuns).toHaveBeenCalledTimes(2);

    fireEvent.click(refreshingButton as HTMLButtonElement);
    expect(mockWorkflowActivityApi.getRun).toHaveBeenCalledTimes(2);
    expect(mockWorkflowActivityApi.getRunGraph).toHaveBeenCalledTimes(2);
    expect(mockWorkflowActivityApi.listRuns).toHaveBeenCalledTimes(2);

    await act(async () => {
      detailRefresh.resolve(buildRunDetail());
      graphRefresh.resolve({
        rootNodeId: 'node-root',
        nodes: [
          { nodeId: 'node-root', nodeType: 'step', stepId: 'step-root' },
          {
            nodeId: 'node-failed',
            nodeType: 'step',
            stepId: 'step-failed',
          },
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
      historyRefresh.resolve([buildRunDetail().summary]);
      await Promise.all([
        detailRefresh.promise,
        graphRefresh.promise,
        historyRefresh.promise,
      ]);
    });

    await waitFor(() =>
      expect(mockConsoleToast.success).toHaveBeenCalledWith(
        'Run details refreshed',
        { key: 'run-detail-refresh' },
      ),
    );
    expect(mockConsoleToast.error).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeEnabled();
    expect(
      document.querySelector('.aevatar-loading-overlay'),
    ).not.toBeInTheDocument();
    expect(document.querySelector('.wa-vnext-run-detail')).toHaveAttribute(
      'aria-busy',
      'false',
    );
    expect(
      document.querySelector('.wa-vnext-run-detail__refresh-content'),
    ).not.toHaveAttribute('inert');
  });

  it('reports a partial refresh failure without claiming success', async () => {
    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    const refreshButton = await screen.findByRole('button', {
      name: 'Refresh',
    });
    mockWorkflowActivityApi.getRunGraph.mockRejectedValueOnce(
      new Error('Run graph refresh failed'),
    );

    fireEvent.click(refreshButton);

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Some run details couldn't be refreshed",
        { key: 'run-detail-refresh' },
      ),
    );
    expect(mockConsoleToast.success).not.toHaveBeenCalled();
    expect(screen.queryByText('Run graph unavailable')).not.toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Incident review' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeEnabled();
  });

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

  it('bounds the published-runs rail inside the run detail viewport and scrolls its history list', async () => {
    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    expect(
      await screen.findByRole('heading', { name: 'Published runs' }),
    ).toBeInTheDocument();

    expect(document.querySelector('main.wa-vnext__main')).toHaveClass(
      'wa-vnext__main--run-detail',
    );
    expect(document.querySelector('.wa-vnext__content')).toHaveClass(
      'wa-vnext__content--run-detail',
    );
    expect(document.querySelector('.wa-vnext-run-detail')).toHaveClass(
      'wa-vnext-run-detail--bounded',
    );

    const railList = document.querySelector('.wa-vnext-run-detail__rail-list');
    expect(railList).toBeInstanceOf(HTMLElement);
    expect(railList).toHaveStyle({
      overflowY: 'auto',
    });
  });

  it('uses vNext panel tokens instead of legacy published-runs detail colors', async () => {
    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    expect(
      await screen.findByRole('heading', { name: 'Published runs' }),
    ).toBeInTheDocument();

    const styleText = Array.from(document.querySelectorAll('style'))
      .map((style) => style.textContent ?? '')
      .join('\n');
    expect(styleText).toContain('background: var(--wa-surface);');
    expect(styleText).toContain('border: 1px solid var(--wa-line);');
    expect(styleText).toContain('border-radius: var(--wa-radius);');
    expect(styleText).toContain(
      '.wa-vnext-run-detail__run--selected { background: var(--wa-blue-bg);',
    );
    expect(styleText).not.toContain('background: #f7f8fa;');
    expect(styleText).not.toContain('border-color: #83b7ff;');
  });

  it('keeps the selected workflow history visible when the immutable detail request fails', async () => {
    mockSearch = '?workflowId=wf-alpha';
    mockWorkflowActivityApi.getRun.mockRejectedValue(
      new Error(
        'Error occurred while trying to proxy: 127.0.0.1:5173/api/workflow/observatory/runs/run-source-alpha',
      ),
    );
    mockWorkflowActivityApi.listActivityRuns.mockResolvedValue({
      items: [
        buildActivityRow(),
        buildActivityRow({
          runId: 'run-source-beta',
          status: 'completed',
          success: true,
          updatedAtUtc: '2026-08-04T09:01:00Z',
        }),
      ],
      nextCursor: null,
      hasMore: false,
      totalCount: 2,
    });

    renderWithQueryClient(
      <RunDetailPage runId="run-source-alpha" scopeId="scope-alpha" />,
    );

    await waitFor(() =>
      expect(mockWorkflowActivityApi.listActivityRuns).toHaveBeenCalledWith(
        'scope-alpha',
        {
          workflowId: 'wf-alpha',
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
    expect(
      screen.getByText('Detailed run data is temporarily unavailable.'),
    ).toBeInTheDocument();
    expect(screen.getByText('Approval timed out')).toBeInTheDocument();

    const selectedRun = screen.getByRole('button', {
      name: 'Open run-source-alpha',
    });
    expect(selectedRun).toHaveAttribute('aria-current', 'true');

    fireEvent.click(
      screen.getByRole('button', {
        name: 'Open run-source-beta',
      }),
    );

    expect(history.push).toHaveBeenLastCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity/run-source-beta?workflowId=wf-alpha',
    );
  });
});
