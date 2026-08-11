import { QueryClient } from '@tanstack/react-query';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import * as React from 'react';
import { history } from '@/shared/navigation/history';
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from '../../../../tests/reactQueryTestUtils';
import ActivityPage from './ActivityPage';

let mockSearch = '';

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
    code?: string;
    status: number;

    constructor(message: string, status: number, code?: string) {
      super(message);
      this.code = code;
      this.status = status;
    }
  }

  return {
    WorkflowActivityApiError,
    workflowActivityApi: { listActivityRuns: jest.fn() },
  };
});

jest.mock('@/shared/navigation/history', () => ({
  history: { push: jest.fn(), replace: jest.fn() },
}));

jest.mock('@/shared/ui/ConsoleHeaderActions', () => ({
  ConsoleAuthActions: () => <button type="button">Account</button>,
  ConsoleLanguageSwitch: () => <button type="button">Language</button>,
}));

jest.mock('../hooks/useConsoleLocation', () => ({
  useConsoleLocation: () => ({
    hash: '',
    pathname: '/scopes/scope-alpha/workflow-activity-vnext/activity',
    search: mockSearch,
  }),
}));

const mockListActivityRuns = jest.requireMock(
  '@/shared/api/workflowActivityApi',
).workflowActivityApi.listActivityRuns as jest.Mock;

describe('Workflow Activity vNext Activity ledger', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockSearch = '';
    mockListActivityRuns.mockResolvedValue(feedPage([]));
  });

  afterEach(() => cleanupTestQueryClients());

  it('renders the activity table skeleton while the first page is loading', () => {
    mockListActivityRuns.mockImplementation(() => new Promise(() => {}));

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(screen.getByRole('status')).toHaveAttribute('data-variant', 'table');
    expect(screen.getAllByTestId('aevatar-content-skeleton-cell').length).toBe(
      24,
    );
    expect(screen.getByText('Loading activity…')).toHaveClass(
      'aevatar-loading-visually-hidden',
    );
    expect(
      screen.getByRole('searchbox', { name: 'Filter loaded runs' }),
    ).toBeEnabled();
  });

  it('refreshes the first page when returning to Activity', async () => {
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          gcTime: Infinity,
          refetchOnWindowFocus: false,
          retry: false,
          staleTime: 30_000,
        },
      },
    });
    const firstView = renderWithQueryClient(
      <ActivityPage scopeId="scope-alpha" />,
      queryClient,
    );

    await waitFor(() => expect(mockListActivityRuns).toHaveBeenCalledTimes(1));
    firstView.unmount();

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />, queryClient);

    await waitFor(() => expect(mockListActivityRuns).toHaveBeenCalledTimes(2));
  });

  it('passes a URL workflow identity directly to the Activity feed', async () => {
    mockSearch = '?workflowId=wf-alpha';

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    await waitFor(() =>
      expect(mockListActivityRuns).toHaveBeenCalledWith('scope-alpha', {
        status: undefined,
        origins: undefined,
        definitionActorIds: undefined,
        workflowId: 'wf-alpha',
        fromUtc: undefined,
        toUtc: undefined,
        take: 50,
        cursor: undefined,
        includeTotalCount: true,
      }),
    );

    fireEvent.click(
      screen.getByRole('button', { name: 'Remove workflow filter wf-alpha' }),
    );
    expect(history.replace).toHaveBeenLastCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity',
    );
  });

  it('does not query global runs when the workflow filter is empty', async () => {
    mockSearch = '?workflowId=';

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(
      await screen.findByText('Choose a workflow to filter Activity'),
    ).toBeInTheDocument();
    expect(mockListActivityRuns).not.toHaveBeenCalled();
  });

  it('sends URL-backed filters and total-count intent to the feed', async () => {
    mockSearch =
      '?status=failed&origin=draft&definition=definition-alpha&from=2026-08-01T00%3A00%3A00Z&to=2026-08-05T00%3A00%3A00Z';

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    await waitFor(() =>
      expect(mockListActivityRuns).toHaveBeenCalledWith('scope-alpha', {
        status: 'failed',
        origins: ['draft'],
        definitionActorIds: ['definition-alpha'],
        workflowId: undefined,
        fromUtc: '2026-08-01T00:00:00Z',
        toUtc: '2026-08-05T00:00:00Z',
        take: 50,
        cursor: undefined,
        includeTotalCount: true,
      }),
    );
    expect(
      screen.getByRole('button', { name: 'Show all workflows' }),
    ).toBeEnabled();
    expect(screen.getByPlaceholderText('Activity after')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Activity before')).toBeInTheDocument();
  });

  it('drops an unsupported URL status before querying or preserving it', async () => {
    mockSearch = '?status=waiting&origin=draft';

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    await waitFor(() =>
      expect(mockListActivityRuns).toHaveBeenCalledWith(
        'scope-alpha',
        expect.objectContaining({ status: undefined, origins: ['draft'] }),
      ),
    );
    expect(mockListActivityRuns).not.toHaveBeenCalledWith(
      'scope-alpha',
      expect.objectContaining({ status: 'waiting' }),
    );
    await waitFor(() =>
      expect(history.replace).toHaveBeenLastCalledWith(
        '/scopes/scope-alpha/workflow-activity-vnext/activity?origin=draft',
      ),
    );
  });

  it('labels local search as filtering only loaded runs', async () => {
    mockSearch = '?q=customer&status=failed';
    mockListActivityRuns.mockResolvedValue(
      feedPage([
        activityRow({
          runId: 'run-customer',
          workflowName: 'Customer follow-up',
        }),
        activityRow({ runId: 'run-invoice', workflowName: 'Invoice review' }),
      ]),
    );

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(
      screen.getByRole('searchbox', { name: 'Filter loaded runs' }),
    ).toHaveValue('customer');
    expect(await screen.findByText('Customer follow-up')).toBeInTheDocument();
    expect(screen.queryByText('Invoice review')).not.toBeInTheDocument();
    expect(mockListActivityRuns).toHaveBeenCalledWith(
      'scope-alpha',
      expect.not.objectContaining({ q: 'customer' }),
    );
  });

  it('shows authoritative triage facts without exposing actor identity', async () => {
    mockListActivityRuns.mockResolvedValue(
      feedPage(
        [
          activityRow({
            runId: 'workflow-definition:studio:run:internal-alpha',
            workflowName: 'Customer follow-up',
            runOrigin: 'backend-native-origin.v2',
          }),
        ],
        { totalCount: 42 },
      ),
    );

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(
      await screen.findByRole('button', { name: 'Open Customer follow-up' }),
    ).toBeEnabled();
    expect(screen.getByText('Completed')).toBeInTheDocument();
    expect(screen.getByText('Connector unavailable')).toBeInTheDocument();
    expect(screen.getByText('Abigail')).toBeInTheDocument();
    expect(screen.getByText('backend-native-origin.v2')).toBeInTheDocument();
    expect(screen.getByText('Ticket redacted')).toBeInTheDocument();
    expect(screen.getByText('2m')).toBeInTheDocument();
    expect(screen.getByText('internal-alpha')).toBeInTheDocument();
    expect(screen.getByText('1 of 42 runs loaded')).toBeInTheDocument();
    expect(screen.queryByText('actor-technical-alpha')).not.toBeInTheDocument();
    expect(
      screen.queryByText('workflow-definition:studio:run:internal-alpha'),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('State version')).not.toBeInTheDocument();

    const activityRegion = screen.getByRole('region', { name: 'Activity' });
    expect(activityRegion).toHaveAttribute('tabindex', '0');
    expect(
      within(activityRegion).getByText('Customer follow-up').closest('td'),
    ).toHaveAttribute('data-label', 'Workflow');
  });

  it('loads the next cursor page and appends its rows', async () => {
    mockListActivityRuns
      .mockResolvedValueOnce(
        feedPage(
          [activityRow({ runId: 'run-one', workflowName: 'First run' })],
          {
            hasMore: true,
            nextCursor: 'cursor-two',
            totalCount: 2,
          },
        ),
      )
      .mockResolvedValueOnce(
        feedPage([
          activityRow({ runId: 'run-two', workflowName: 'Second run' }),
        ]),
      );

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));

    expect(await screen.findByText('Second run')).toBeInTheDocument();
    expect(screen.getByText('First run')).toBeInTheDocument();
    expect(mockListActivityRuns).toHaveBeenLastCalledWith(
      'scope-alpha',
      expect.objectContaining({
        cursor: 'cursor-two',
        includeTotalCount: false,
      }),
    );
  });

  it('preserves loaded rows when the next page fails and allows retry', async () => {
    mockListActivityRuns
      .mockResolvedValueOnce(
        feedPage(
          [activityRow({ runId: 'run-one', workflowName: 'First run' })],
          {
            hasMore: true,
            nextCursor: 'cursor-two',
          },
        ),
      )
      .mockRejectedValueOnce(new Error('Network unavailable'))
      .mockResolvedValueOnce(
        feedPage([
          activityRow({ runId: 'run-two', workflowName: 'Second run' }),
        ]),
      );

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));
    expect(
      await screen.findByText("Couldn't load more runs"),
    ).toBeInTheDocument();
    expect(screen.getByText('First run')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Retry loading more' }));
    expect(await screen.findByText('Second run')).toBeInTheDocument();
  });

  it('refreshes from the first page when an opaque cursor is rejected', async () => {
    const { WorkflowActivityApiError } = jest.requireMock(
      '@/shared/api/workflowActivityApi',
    );
    mockListActivityRuns
      .mockResolvedValueOnce(
        feedPage(
          [activityRow({ runId: 'run-one', workflowName: 'First run' })],
          {
            hasMore: true,
            nextCursor: 'cursor-invalid',
          },
        ),
      )
      .mockRejectedValueOnce(
        new WorkflowActivityApiError(
          'The cursor is malformed.',
          400,
          'malformed_cursor',
        ),
      )
      .mockResolvedValueOnce(
        feedPage([
          activityRow({ runId: 'run-fresh', workflowName: 'Fresh first page' }),
        ]),
      );

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));
    fireEvent.click(
      await screen.findByRole('button', { name: 'Refresh from start' }),
    );

    expect(await screen.findByText('Fresh first page')).toBeInTheDocument();
    expect(mockListActivityRuns).toHaveBeenLastCalledWith(
      'scope-alpha',
      expect.objectContaining({ cursor: undefined, includeTotalCount: true }),
    );
  });

  it('renders unrecognized returned run states as Unknown', async () => {
    mockListActivityRuns.mockResolvedValue(
      feedPage([activityRow({ status: 'future_waiting_state' })]),
    );

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(await screen.findByText('Unknown')).toBeInTheDocument();
    expect(screen.queryByText('future_waiting_state')).not.toBeInTheDocument();
  });
});

function feedPage(
  items: readonly ReturnType<typeof activityRow>[],
  overrides: {
    readonly hasMore?: boolean;
    readonly nextCursor?: string | null;
    readonly totalCount?: number | null;
  } = {},
) {
  return {
    items,
    nextCursor: overrides.nextCursor ?? null,
    hasMore: overrides.hasMore ?? false,
    totalCount: overrides.totalCount ?? items.length,
  };
}

function activityRow(
  overrides: Partial<{
    runId: string;
    workflowName: string;
    runOrigin: string;
    status: string;
  }> = {},
) {
  return {
    runId: overrides.runId ?? 'run-alpha',
    actorId: 'actor-technical-alpha',
    workflowId: 'wf-alpha',
    workflowName: overrides.workflowName ?? 'Customer follow-up',
    scopeId: 'scope-alpha',
    status: overrides.status ?? 'completed',
    runOrigin: overrides.runOrigin ?? 'draft',
    success: true,
    initiator: {
      platform: 'nyxid',
      tenant: 'tenant-alpha',
      externalUserId: 'user-alpha',
      scope: 'scope-alpha',
      bindingId: 'binding-alpha',
      displayValue: 'Abigail',
      availability: 'available',
    },
    inputSummary: 'Ticket redacted',
    currentStep: {
      stepId: 'step-failed',
      inputSummary: 'Connector request',
      availability: 'available',
    },
    firstFailure: {
      stepId: 'step-failed',
      message: 'Connector unavailable',
      availability: 'available',
    },
    waiting: {
      stepId: '',
      waitingKind: '',
      prompt: '',
      availability: 'unavailable',
    },
    startedAtUtc: '2026-08-04T09:58:00Z',
    completedAtUtc: '2026-08-04T10:00:00Z',
    updatedAtUtc: '2026-08-04T10:00:00Z',
    durationMs: 120000,
    stateVersion: 18,
    recoveryCapability: {
      retryFailedStep: recoveryAction(),
      runAgain: recoveryAction(),
      workflowDefinitionRevisionId: 'revision-3',
      workflowDefinitionVersion: 3,
    },
    lineage: {
      availability: 1,
      retryFork: {
        availability: 2,
        sourceRunId: '',
        originalRunId: '',
        attempt: 0,
        startAtStepId: '',
        childRuns: [],
      },
      subWorkflow: {
        availability: 2,
        parentRunId: '',
        parentActorId: '',
        parentStepId: '',
        rootRunId: '',
        depth: 0,
        childRuns: [],
      },
      unavailableReason: '',
    },
  };
}

function recoveryAction() {
  return {
    eligibility: 2,
    unavailableReasonCode: 1,
    unavailableReason: '',
    recommendedActions: [],
    startingStepId: '',
    reusesPriorStepOutputs: false,
    mayIncurModelOrToolCost: false,
  };
}
