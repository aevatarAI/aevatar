import { QueryClient } from '@tanstack/react-query';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import * as React from 'react';
import { history } from '@/shared/navigation/history';
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from '../../../../tests/reactQueryTestUtils';
import { workflowActivityVNextCss } from '../styles';
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
      this.name = 'WorkflowActivityApiError';
      Object.setPrototypeOf(this, WorkflowActivityApiError.prototype);
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
const MockWorkflowActivityApiError = jest.requireMock(
  '@/shared/api/workflowActivityApi',
).WorkflowActivityApiError as new (
  message: string,
  status: number,
  code?: string,
) => Error;

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
      20,
    );
    expect(screen.getByText('Loading activity…')).toHaveClass(
      'aevatar-loading-visually-hidden',
    );
    expect(
      screen.getByRole('searchbox', { name: 'Search runs' }),
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

  it('retries one transient Activity feed failure before showing an error', async () => {
    mockSearch = '?workflowId=wf-alpha&schedule=schedule-alpha';
    mockListActivityRuns
      .mockRejectedValueOnce(
        new MockWorkflowActivityApiError(
          'Error occurred while trying to proxy: localhost:5175/api/workflow/observatory/activity-runs',
          502,
        ),
      )
      .mockResolvedValueOnce(feedPage([]));

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(await screen.findByText('No matching runs')).toBeInTheDocument();
    expect(mockListActivityRuns).toHaveBeenCalledTimes(2);
    expect(screen.queryByText('Activity unavailable')).not.toBeInTheDocument();
  });

  it('does not retry an unauthorized Activity feed request', async () => {
    mockListActivityRuns.mockRejectedValueOnce(
      new MockWorkflowActivityApiError('Unauthorized', 401),
    );

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(mockListActivityRuns).toHaveBeenCalledTimes(1);
  });

  it('passes a URL workflow identity directly to the Activity feed', async () => {
    mockSearch = '?workflowId=wf-alpha';

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    await waitFor(() =>
      expect(mockListActivityRuns).toHaveBeenCalledWith('scope-alpha', {
        status: undefined,
        origins: undefined,
        definitionActorIds: undefined,
        scheduleIds: undefined,
        workflowId: 'wf-alpha',
        searchText: undefined,
        fromUtc: undefined,
        toUtc: undefined,
        take: 25,
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

  it('filters by Schedule without inventing a Run source', async () => {
    mockSearch = '?workflowId=wf-alpha&schedule=schedule-alpha';

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    await waitFor(() =>
      expect(mockListActivityRuns).toHaveBeenCalledWith('scope-alpha', {
        status: undefined,
        origins: undefined,
        definitionActorIds: undefined,
        scheduleIds: ['schedule-alpha'],
        workflowId: 'wf-alpha',
        searchText: undefined,
        fromUtc: undefined,
        toUtc: undefined,
        take: 25,
        cursor: undefined,
        includeTotalCount: true,
      }),
    );

    expect(
      screen.getByRole('button', { name: 'Remove workflow filter wf-alpha' }),
    ).toBeVisible();
    expect(
      screen.getByRole('button', {
        name: 'Remove schedule filter schedule-alpha',
      }),
    ).toBeVisible();
    const scopeContext = document.querySelector(
      '.wa-vnext__activity-filter-context',
    );
    expect(scopeContext).toBeInTheDocument();
    expect(scopeContext).toContainElement(
      screen.getByRole('button', {
        name: 'Remove workflow filter wf-alpha',
      }),
    );
    expect(scopeContext).toContainElement(
      screen.getByRole('button', {
        name: 'Remove schedule filter schedule-alpha',
      }),
    );
    expect(workflowActivityVNextCss).toContain(
      '.wa-vnext__activity-filter-context { margin-bottom: 12px; }',
    );
    expect(
      screen.queryByRole('button', { name: /Remove source filter/ }),
    ).not.toBeInTheDocument();

    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Run source' }));
    await screen.findByText('Chat', {
      selector: '.ant-select-item-option-content',
    });
    expect(
      screen.queryByText('Schedule', {
        selector: '.ant-select-item-option-content',
      }),
    ).not.toBeInTheDocument();

    fireEvent.click(
      screen.getByRole('button', {
        name: 'Remove schedule filter schedule-alpha',
      }),
    );
    expect(history.replace).toHaveBeenLastCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity?workflowId=wf-alpha',
    );
  });

  it('drops the legacy Schedule origin from Activity links', async () => {
    mockSearch = '?workflowId=wf-alpha&schedule=schedule-alpha&origin=schedule';

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    await waitFor(() =>
      expect(mockListActivityRuns).toHaveBeenCalledWith(
        'scope-alpha',
        expect.objectContaining({
          origins: undefined,
          scheduleIds: ['schedule-alpha'],
          workflowId: 'wf-alpha',
        }),
      ),
    );
    expect(history.replace).toHaveBeenLastCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity?workflowId=wf-alpha&schedule=schedule-alpha',
    );
    expect(
      screen.queryByRole('button', { name: /Remove source filter/ }),
    ).not.toBeInTheDocument();
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
      '?status=failed&origin=draft&definition=definition-alpha&schedule=schedule-alpha&q=customer&from=2026-08-01T00%3A00%3A00Z&to=2026-08-05T00%3A00%3A00Z';

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    await waitFor(() =>
      expect(mockListActivityRuns).toHaveBeenCalledWith('scope-alpha', {
        status: 'failed',
        origins: ['draft'],
        definitionActorIds: ['definition-alpha'],
        scheduleIds: ['schedule-alpha'],
        workflowId: undefined,
        searchText: 'customer',
        fromUtc: '2026-08-01T00:00:00Z',
        toUtc: '2026-08-05T00:00:00Z',
        take: 25,
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

  it('sends the URL search text to the Activity feed', async () => {
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

    expect(screen.getByRole('searchbox', { name: 'Search runs' })).toHaveValue(
      'customer',
    );
    expect(await screen.findByText('Customer follow-up')).toBeInTheDocument();
    expect(mockListActivityRuns).toHaveBeenCalledWith(
      'scope-alpha',
      expect.objectContaining({ searchText: 'customer' }),
    );
  });

  it('waits for Search before submitting Activity filter changes', async () => {
    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    await waitFor(() => expect(mockListActivityRuns).toHaveBeenCalledTimes(1));

    const searchInput = screen.getByRole('searchbox', {
      name: 'Search runs',
    });
    fireEvent.change(searchInput, { target: { value: 'support' } });
    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Run status' }));
    fireEvent.click(await screen.findByText('Failed'));

    expect(searchInput).toHaveValue('support');
    expect(history.replace).not.toHaveBeenCalled();
    expect(mockListActivityRuns).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole('button', { name: 'Search' }));

    expect(history.replace).toHaveBeenLastCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity?q=support&status=failed',
    );
    expect(mockListActivityRuns).toHaveBeenCalledTimes(1);
  });

  it('keeps the activity list focused on user-facing run facts', async () => {
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

    await screen.findByText('Customer follow-up');
    expect(
      screen.queryByRole('button', { name: 'Open Customer follow-up' }),
    ).not.toBeInTheDocument();
    expect(screen.getByText('Completed')).toBeInTheDocument();
    expect(screen.getByText('Connector unavailable')).toBeInTheDocument();
    expect(screen.getByText('Ticket redacted')).toBeInTheDocument();
    expect(screen.getByText('2m')).toBeInTheDocument();
    expect(screen.getByText('Page 1 of 2')).toBeInTheDocument();
    expect(
      screen.getByRole('columnheader', { name: 'Duration' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('columnheader', { name: 'Input preview' }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('columnheader', { name: 'Initiator' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByText('generate_weekly_report'),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByText('backend-native-origin.v2'),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('internal-alpha')).not.toBeInTheDocument();
    expect(
      screen.queryByText('workflow-definition:studio:run:internal-alpha'),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('State version')).not.toBeInTheDocument();

    expect(screen.getByText('Ticket redacted')).toHaveClass(
      'wa-vnext__input-preview',
    );

    const activityRegion = screen.getByRole('region', { name: 'Activity' });
    expect(activityRegion).toHaveAttribute('tabindex', '0');
    expect(activityRegion).toHaveClass('wa-vnext__activity-table-region');
    expect(
      within(activityRegion).getByText('Customer follow-up').closest('td'),
    ).toHaveAttribute('data-label', 'Workflow');
  });

  it('opens run details when an activity row is clicked', async () => {
    mockListActivityRuns.mockResolvedValue(
      feedPage([
        activityRow({
          runId: 'run-customer',
          workflowName: 'Customer follow-up',
        }),
      ]),
    );

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    const customerRunRow = (
      await screen.findByText('Customer follow-up')
    ).closest('tr');
    if (!customerRunRow) throw new Error('The customer run row was not found.');

    fireEvent.click(customerRunRow);

    expect(history.push).toHaveBeenLastCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity/run-customer?workflowId=wf-alpha',
    );
  });

  it('opens run details when an activity row is keyboard activated', async () => {
    mockListActivityRuns.mockResolvedValue(
      feedPage([
        activityRow({
          runId: 'run-customer',
          workflowName: 'Customer follow-up',
        }),
      ]),
    );

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    const customerRunRow = (
      await screen.findByText('Customer follow-up')
    ).closest('tr');
    if (!customerRunRow) throw new Error('The customer run row was not found.');
    expect(customerRunRow).toHaveAttribute('tabindex', '0');

    fireEvent.keyDown(customerRunRow, { key: 'Enter' });
    expect(history.push).toHaveBeenLastCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity/run-customer?workflowId=wf-alpha',
    );

    fireEvent.keyDown(customerRunRow, { key: ' ' });
    expect(history.push).toHaveBeenCalledTimes(2);
  });

  it('renders a standard pagination control with a direct page jump input', async () => {
    mockListActivityRuns.mockResolvedValue(
      feedPage([activityRow({ runId: 'run-one', workflowName: 'First run' })], {
        hasMore: true,
        nextCursor: 'cursor-two',
        totalCount: 75,
      }),
    );

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    const pagination = await screen.findByTestId('activity-pagination');
    expect(pagination).toHaveClass('ant-pagination');
    expect(within(pagination).getByTitle('3')).toBeInTheDocument();
    expect(
      pagination.querySelector('.ant-pagination-options-quick-jumper input'),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Load more' }),
    ).not.toBeInTheDocument();
  });

  it('does not expose arbitrary page jumping when the feed omits its total count', async () => {
    mockListActivityRuns.mockResolvedValue({
      hasMore: true,
      items: [activityRow({ runId: 'run-one', workflowName: 'First run' })],
      nextCursor: 'cursor-two',
      totalCount: null,
    });

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    const pagination = await screen.findByTestId('activity-pagination');
    expect(
      pagination.querySelector('.ant-pagination-options-quick-jumper input'),
    ).not.toBeInTheDocument();
  });

  it('opens a selected known page with its saved cursor', async () => {
    mockListActivityRuns
      .mockResolvedValueOnce(
        feedPage(
          [activityRow({ runId: 'run-one', workflowName: 'First run' })],
          {
            hasMore: true,
            nextCursor: 'cursor-two',
            totalCount: 26,
          },
        ),
      )
      .mockResolvedValueOnce(
        feedPage(
          [activityRow({ runId: 'run-two', workflowName: 'Second run' })],
          { totalCount: 26 },
        ),
      );

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    fireEvent.click(
      within(await screen.findByTestId('activity-pagination')).getByTitle('2'),
    );

    expect(await screen.findByText('Second run')).toBeInTheDocument();
    expect(screen.queryByText('First run')).not.toBeInTheDocument();
    expect(mockListActivityRuns).toHaveBeenLastCalledWith(
      'scope-alpha',
      expect.objectContaining({
        cursor: 'cursor-two',
        includeTotalCount: true,
      }),
    );
    expect(
      document.querySelector('.wa-vnext__activity-footer'),
    ).toHaveTextContent('Page 2 of 2');
  });

  it('resolves missing cursors before opening a directly selected page', async () => {
    mockListActivityRuns
      .mockResolvedValueOnce(
        feedPage(
          [activityRow({ runId: 'run-one', workflowName: 'First run' })],
          {
            hasMore: true,
            nextCursor: 'cursor-two',
            totalCount: 75,
          },
        ),
      )
      .mockResolvedValueOnce(
        feedPage(
          [activityRow({ runId: 'run-two', workflowName: 'Second run' })],
          {
            hasMore: true,
            nextCursor: 'cursor-three',
            totalCount: 75,
          },
        ),
      )
      .mockResolvedValueOnce(
        feedPage(
          [activityRow({ runId: 'run-three', workflowName: 'Third run' })],
          { totalCount: 75 },
        ),
      );

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    const quickJumper = (
      await screen.findByTestId('activity-pagination')
    ).querySelector('.ant-pagination-options-quick-jumper input');
    if (!quickJumper) throw new Error('The page jump input was not found.');
    fireEvent.change(quickJumper, { target: { value: '3' } });
    fireEvent.keyUp(quickJumper, { key: 'Enter', keyCode: 13 });

    expect(await screen.findByText('Third run')).toBeInTheDocument();
    expect(screen.queryByText('First run')).not.toBeInTheDocument();
    expect(screen.queryByText('Second run')).not.toBeInTheDocument();
    expect(mockListActivityRuns).toHaveBeenCalledTimes(3);
    expect(
      mockListActivityRuns.mock.calls.map(([, filter]) => filter.cursor),
    ).toEqual([undefined, 'cursor-two', 'cursor-three']);
    expect(
      document.querySelector('.wa-vnext__activity-footer'),
    ).toHaveTextContent('Page 3 of 3');
  });

  it('keeps the current page visible and retries when cursor resolution fails', async () => {
    mockListActivityRuns
      .mockResolvedValueOnce(
        feedPage(
          [activityRow({ runId: 'run-one', workflowName: 'First run' })],
          {
            hasMore: true,
            nextCursor: 'cursor-invalid',
            totalCount: 75,
          },
        ),
      )
      .mockRejectedValueOnce(new Error('Network unavailable'))
      .mockResolvedValueOnce(
        feedPage(
          [activityRow({ runId: 'run-two', workflowName: 'Second run' })],
          { hasMore: true, nextCursor: 'cursor-three', totalCount: 75 },
        ),
      )
      .mockResolvedValueOnce(
        feedPage(
          [activityRow({ runId: 'run-three', workflowName: 'Third run' })],
          { totalCount: 75 },
        ),
      );

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    const quickJumper = (
      await screen.findByTestId('activity-pagination')
    ).querySelector('.ant-pagination-options-quick-jumper input');
    if (!quickJumper) throw new Error('The page jump input was not found.');
    fireEvent.change(quickJumper, { target: { value: '3' } });
    fireEvent.keyUp(quickJumper, { key: 'Enter', keyCode: 13 });

    expect(
      await screen.findByText("Couldn't load this page"),
    ).toBeInTheDocument();
    expect(screen.getByText('First run')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));

    expect(await screen.findByText('Third run')).toBeInTheDocument();
    expect(
      mockListActivityRuns.mock.calls.map(([, filter]) => filter.cursor),
    ).toEqual([undefined, 'cursor-invalid', 'cursor-invalid', 'cursor-three']);
  });

  it('resets cursor pagination to the first page when filters change', async () => {
    mockListActivityRuns
      .mockResolvedValueOnce(
        feedPage(
          [activityRow({ runId: 'run-one', workflowName: 'First run' })],
          { hasMore: true, nextCursor: 'cursor-two', totalCount: 26 },
        ),
      )
      .mockResolvedValueOnce(
        feedPage(
          [activityRow({ runId: 'run-two', workflowName: 'Second run' })],
          { totalCount: 26 },
        ),
      )
      .mockResolvedValueOnce(
        feedPage(
          [
            activityRow({
              runId: 'run-filtered',
              workflowName: 'Filtered run',
            }),
          ],
          { totalCount: 1 },
        ),
      );

    const view = renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    fireEvent.click(
      within(await screen.findByTestId('activity-pagination')).getByTitle('2'),
    );
    await screen.findByText('Second run');

    mockSearch = '?status=failed';
    view.unmount();
    renderWithQueryClient(
      <ActivityPage scopeId="scope-alpha" />,
      view.queryClient,
    );

    expect(await screen.findByText('Filtered run')).toBeInTheDocument();
    expect(mockListActivityRuns).toHaveBeenLastCalledWith(
      'scope-alpha',
      expect.objectContaining({ status: 'failed', cursor: undefined }),
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
