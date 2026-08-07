import {
  act,
  fireEvent,
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
    status: number;

    constructor(message: string, status: number) {
      super(message);
      this.status = status;
    }
  }

  return {
    WorkflowActivityApiError,
    workflowActivityApi: { listRuns: jest.fn() },
  };
});

jest.mock('@/shared/api/scopesApi', () => ({
  scopesApi: { getWorkflowDetail: jest.fn() },
}));

jest.mock('@/shared/navigation/history', () => ({
  history: { push: jest.fn(), replace: jest.fn() },
}));

const mockConsoleToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};

jest.mock('@/shared/ui/ConsoleToast', () => ({
  ...jest.requireActual('@/shared/ui/ConsoleToast'),
  useConsoleToast: () => mockConsoleToast,
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

const mockListRuns = jest.requireMock('@/shared/api/workflowActivityApi')
  .workflowActivityApi.listRuns as jest.Mock;
const mockGetWorkflowDetail = jest.requireMock('@/shared/api/scopesApi')
  .scopesApi.getWorkflowDetail as jest.Mock;
const mockWriteText = jest.fn();

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((next) => {
    resolve = next;
  });
  return { promise, resolve };
}

function runSummary(
  overrides: Partial<{
    runId: string;
    workflowName: string;
    status: string;
    success: boolean | null;
    startedAtUtc: string | null;
    updatedAtUtc: string;
    stateVersion: number;
    scopeId: string;
    runOrigin: string;
  }> = {},
) {
  return {
    runId: 'workflow-definition:studio:run:alpha-1234567890',
    workflowName: 'Customer follow-up',
    status: 'completed',
    success: true,
    startedAtUtc: '2026-08-04T10:00:00Z',
    updatedAtUtc: '2026-08-04T10:01:00Z',
    stateVersion: 21,
    scopeId: 'scope-alpha',
    runOrigin: 'backend-origin.v1',
    ...overrides,
  };
}

describe('Workflow Activity vNext Activity ledger', () => {
  beforeEach(() => {
    Object.defineProperty(window.navigator, 'clipboard', {
      configurable: true,
      value: { writeText: mockWriteText },
    });
    Object.defineProperty(document, 'execCommand', {
      configurable: true,
      value: undefined,
    });
    jest.clearAllMocks();
    mockSearch = '';
    mockListRuns.mockResolvedValue([]);
  });

  afterEach(() => cleanupTestQueryClients());

  it('renders the activity table skeleton while the run list is loading', () => {
    mockListRuns.mockImplementation(() => new Promise(() => {}));

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(screen.getByRole('status')).toHaveAttribute('data-variant', 'table');
    expect(screen.getAllByTestId('aevatar-content-skeleton-cell')).toHaveLength(
      20,
    );
    expect(screen.getByText('Loading activity…')).toHaveClass(
      'aevatar-loading-visually-hidden',
    );
    expect(
      screen.getByRole('searchbox', { name: 'Search runs' }),
    ).toBeEnabled();
    expect(screen.queryByText('No runs yet')).not.toBeInTheDocument();
  });

  it('renders the activity table skeleton while resolving a workflow filter', () => {
    mockSearch = '?workflowId=wf-alpha';
    mockGetWorkflowDetail.mockImplementation(() => new Promise(() => {}));

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(screen.getByRole('status')).toHaveAttribute('data-variant', 'table');
    expect(screen.getByText('Loading workflow activity…')).toHaveClass(
      'aevatar-loading-visually-hidden',
    );
    expect(mockListRuns).not.toHaveBeenCalled();
  });

  it('restores a visible workflow filter from the URL and removes it back to global Activity', async () => {
    mockSearch = '?workflowId=wf-alpha';
    mockGetWorkflowDetail.mockResolvedValue({
      available: true,
      scopeId: 'scope-alpha',
      workflow: null,
      source: {
        definitionActorId: 'definition-alpha',
        inlineWorkflowYamls: null,
        workflowYaml: '',
      },
    });

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    await waitFor(() =>
      expect(mockListRuns).toHaveBeenCalledWith('scope-alpha', {
        status: undefined,
        origins: undefined,
        definitionActorIds: ['definition-alpha'],
        fromUtc: undefined,
        toUtc: undefined,
        take: 50,
      }),
    );
    expect(mockGetWorkflowDetail).toHaveBeenCalledWith(
      'scope-alpha',
      'wf-alpha',
    );

    fireEvent.click(
      screen.getByRole('button', {
        name: 'Remove workflow filter wf-alpha',
      }),
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
    expect(mockGetWorkflowDetail).not.toHaveBeenCalled();
    expect(mockListRuns).not.toHaveBeenCalled();
  });

  it('keeps an unresolved workflow filter visible without showing global runs', async () => {
    mockSearch = '?workflowId=wf-missing';
    mockGetWorkflowDetail.mockRejectedValue(new Error('GET returned 404'));

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(
      await screen.findByText('Workflow activity unavailable'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Remove workflow filter wf-missing',
      }),
    ).toBeEnabled();
    expect(mockListRuns).not.toHaveBeenCalled();
  });

  it('keeps a workflow without a definition in an honest unavailable state', async () => {
    mockSearch = '?workflowId=wf-draft-only';
    mockGetWorkflowDetail.mockResolvedValue({
      available: false,
      scopeId: 'scope-alpha',
      workflow: null,
      source: null,
    });

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(
      await screen.findByText('Activity filtering is unavailable'),
    ).toBeInTheDocument();
    expect(mockListRuns).not.toHaveBeenCalled();
  });

  it('ignores the retired source filter and sends only supported URL-backed filters', async () => {
    mockSearch = '?status=failed&origin=draft&definition=definition-alpha';

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    await waitFor(() =>
      expect(mockListRuns).toHaveBeenCalledWith('scope-alpha', {
        status: 'failed',
        origins: undefined,
        definitionActorIds: ['definition-alpha'],
        fromUtc: undefined,
        toUtc: undefined,
        take: 50,
      }),
    );
    expect(
      screen.queryByRole('combobox', { name: 'Run source' }),
    ).not.toBeInTheDocument();
    await waitFor(() =>
      expect(history.replace).toHaveBeenLastCalledWith(
        '/scopes/scope-alpha/workflow-activity-vnext/activity?status=failed&definition=definition-alpha',
      ),
    );
    expect(
      screen.getByRole('button', { name: 'Show all workflows' }),
    ).toBeEnabled();
  });

  it('drops an unsupported URL status before querying or preserving it', async () => {
    mockSearch = '?status=waiting&origin=draft';

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    await waitFor(() =>
      expect(mockListRuns).toHaveBeenLastCalledWith('scope-alpha', {
        status: undefined,
        origins: undefined,
        definitionActorIds: undefined,
        fromUtc: undefined,
        toUtc: undefined,
        take: 50,
      }),
    );
    expect(mockListRuns).not.toHaveBeenCalledWith('scope-alpha', {
      status: 'waiting',
      origins: undefined,
      definitionActorIds: undefined,
      fromUtc: undefined,
      toUtc: undefined,
      take: 50,
    });
    await waitFor(() =>
      expect(history.replace).toHaveBeenLastCalledWith(
        '/scopes/scope-alpha/workflow-activity-vnext/activity',
      ),
    );
  });

  it('restores search from the URL without sending it to the runs API', async () => {
    mockSearch = '?q=customer&status=failed&definition=definition-alpha';

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(screen.getByRole('searchbox', { name: 'Search runs' })).toHaveValue(
      'customer',
    );
    await waitFor(() =>
      expect(mockListRuns).toHaveBeenLastCalledWith('scope-alpha', {
        status: 'failed',
        origins: undefined,
        definitionActorIds: ['definition-alpha'],
        fromUtc: undefined,
        toUtc: undefined,
        take: 50,
      }),
    );

    fireEvent.change(screen.getByRole('searchbox', { name: 'Search runs' }), {
      target: { value: 'invoice' },
    });

    await waitFor(() =>
      expect(history.replace).toHaveBeenLastCalledWith(
        '/scopes/scope-alpha/workflow-activity-vnext/activity?q=invoice&status=failed&definition=definition-alpha',
      ),
    );
    expect(mockListRuns).toHaveBeenLastCalledWith('scope-alpha', {
      status: 'failed',
      origins: undefined,
      definitionActorIds: ['definition-alpha'],
      fromUtc: undefined,
      toUtc: undefined,
      take: 50,
    });
  });

  it('shows product run information without exposing internal observation fields', async () => {
    mockListRuns.mockResolvedValue([
      {
        runId: 'workflow-definition:studio:run:internal-alpha',
        workflowName: 'Customer follow-up',
        status: 'completed',
        success: true,
        startedAtUtc: '2026-08-04T10:00:00Z',
        updatedAtUtc: '2026-08-04T10:01:00Z',
        stateVersion: 21,
        scopeId: 'scope-alpha',
        runOrigin: 'backend-origin.v1',
      },
    ]);

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(
      await screen.findByRole('button', {
        name: 'Open Customer follow-up run intern…lpha',
      }),
    ).toBeEnabled();
    expect(screen.getByText('Completed')).toBeInTheDocument();
    expect(screen.getByText('backend-origin.v1')).toBeInTheDocument();
    expect(
      screen.queryByText('workflow-definition:studio:run:internal-alpha'),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('State version')).not.toBeInTheDocument();
    expect(screen.queryByText(/read model/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/recently observed/i)).not.toBeInTheDocument();

    const activityRegion = screen.getByRole('region', { name: 'Activity' });
    expect(activityRegion).toHaveAttribute('tabindex', '0');
    expect(
      within(activityRegion).getByText('Customer follow-up').closest('td'),
    ).toHaveAttribute('data-label', 'Workflow');
  });

  it('shows a neutral placeholder when the backend run origin is empty', async () => {
    mockListRuns.mockResolvedValue([
      {
        runId: 'run-empty-origin',
        workflowName: 'Legacy workflow',
        status: 'completed',
        success: true,
        startedAtUtc: '2026-08-04T10:00:00Z',
        updatedAtUtc: '2026-08-04T10:01:00Z',
        stateVersion: 21,
        scopeId: 'scope-alpha',
        runOrigin: '',
      },
    ]);

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    const sourceCell = (await screen.findByText('Legacy workflow'))
      .closest('tr')
      ?.querySelector('td[data-label="Source"]');
    expect(sourceCell).toHaveTextContent('-');
  });

  it('renders unrecognized returned run states as Unknown', async () => {
    mockListRuns.mockResolvedValue([
      {
        runId: 'run-unknown-state',
        workflowName: 'Customer follow-up',
        status: 'waiting',
        success: null,
        startedAtUtc: '2026-08-04T10:00:00Z',
        updatedAtUtc: '2026-08-04T10:01:00Z',
        stateVersion: 21,
        scopeId: 'scope-alpha',
        runOrigin: 'draft',
      },
    ]);

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(await screen.findByText('Unknown')).toBeInTheDocument();
    expect(screen.queryByText('Waiting')).not.toBeInTheDocument();
  });

  it('restores URL-backed time filters and sends their UTC bounds to the API', async () => {
    mockSearch = '?from=2026-08-01T09%3A30&to=2026-08-05T18%3A15&status=failed';

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(screen.getByLabelText('Activity after')).toHaveValue(
      '2026-08-01T09:30',
    );
    expect(screen.getByLabelText('Activity before')).toHaveValue(
      '2026-08-05T18:15',
    );
    await waitFor(() =>
      expect(mockListRuns).toHaveBeenLastCalledWith('scope-alpha', {
        status: 'failed',
        origins: undefined,
        definitionActorIds: undefined,
        fromUtc: new Date('2026-08-01T09:30').toISOString(),
        toUtc: new Date('2026-08-05T18:15').toISOString(),
        take: 50,
      }),
    );
  });

  it('loads a larger server-backed page and reports the visible result count', async () => {
    const loadMore = deferred<ReturnType<typeof runSummary>[]>();
    mockListRuns.mockImplementation(
      (_scopeId: string, filter: { take: number }) =>
        filter.take === 50
          ? Promise.resolve(
              Array.from({ length: 50 }, (_, index) =>
                runSummary({
                  runId: `run-${index + 1}`,
                  workflowName: `Workflow ${index + 1}`,
                }),
              ),
            )
          : loadMore.promise,
    );

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(
      await screen.findByText('Showing 50 loaded runs'),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() =>
      expect(mockListRuns).toHaveBeenLastCalledWith(
        'scope-alpha',
        expect.objectContaining({ take: 100 }),
      ),
    );
    expect(screen.getByRole('button', { name: /Load more/ })).toBeDisabled();

    await act(async () => {
      loadMore.resolve(
        Array.from({ length: 51 }, (_, index) =>
          runSummary({
            runId: `run-${index + 1}`,
            workflowName: `Workflow ${index + 1}`,
          }),
        ),
      );
    });

    expect(
      await screen.findByText('Showing 51 loaded runs'),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Load more' }),
    ).not.toBeInTheDocument();
  });

  it('distinguishes duplicate workflow runs and copies the exact run ID', async () => {
    mockWriteText.mockResolvedValue(undefined);
    mockListRuns.mockResolvedValue([
      runSummary({
        runId: 'workflow-definition:studio:run:alpha-1234567890',
      }),
      runSummary({
        runId: 'workflow-definition:studio:run:beta-0987654321',
        startedAtUtc: '2026-08-04T11:00:00Z',
      }),
    ]);

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(
      await screen.findByRole('button', {
        name: 'Open Customer follow-up run alpha-…7890',
      }),
    ).toBeEnabled();
    expect(
      screen.getByRole('button', {
        name: 'Open Customer follow-up run beta-0…4321',
      }),
    ).toBeEnabled();

    fireEvent.click(
      screen.getByRole('button', {
        name: 'Copy run reference alpha-…7890',
      }),
    );
    await waitFor(() =>
      expect(mockWriteText).toHaveBeenCalledWith(
        'workflow-definition:studio:run:alpha-1234567890',
      ),
    );
    expect(mockConsoleToast.success).toHaveBeenCalledWith(
      'Run reference copied.',
    );
  });

  it('uses the fallback copy path when the Clipboard API is unavailable', async () => {
    const fallbackCopy = jest.fn(() => true);
    Object.defineProperty(window.navigator, 'clipboard', {
      configurable: true,
      value: undefined,
    });
    Object.defineProperty(document, 'execCommand', {
      configurable: true,
      value: fallbackCopy,
    });
    mockListRuns.mockResolvedValue([runSummary()]);

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Copy run reference alpha-…7890',
      }),
    );

    await waitFor(() => expect(fallbackCopy).toHaveBeenCalledWith('copy'));
    expect(mockConsoleToast.success).toHaveBeenCalledWith(
      'Run reference copied.',
    );
  });

  it('reports a rejected clipboard write without an unhandled copy state', async () => {
    mockWriteText.mockRejectedValueOnce(new Error('clipboard denied'));
    mockListRuns.mockResolvedValue([runSummary()]);

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Copy run reference alpha-…7890',
      }),
    );

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        'Failed to copy run reference.',
      ),
    );
    expect(mockConsoleToast.success).not.toHaveBeenCalled();
  });

  it('clears prior scope rows while the next scope is loading', async () => {
    const nextScope = deferred<ReturnType<typeof runSummary>[]>();
    mockListRuns.mockImplementation((scopeId: string) =>
      scopeId === 'scope-alpha'
        ? Promise.resolve([runSummary({ workflowName: 'Alpha workflow' })])
        : nextScope.promise,
    );

    const ScopeHarness = () => {
      const [scopeId, setScopeId] = React.useState('scope-alpha');
      return (
        <>
          <button type="button" onClick={() => setScopeId('scope-beta')}>
            Switch scope
          </button>
          <ActivityPage scopeId={scopeId} />
        </>
      );
    };

    renderWithQueryClient(<ScopeHarness />);
    await screen.findByText('Alpha workflow');

    fireEvent.click(screen.getByRole('button', { name: 'Switch scope' }));

    expect(await screen.findByText('Loading activity…')).toBeInTheDocument();
    expect(screen.queryByText('Alpha workflow')).not.toBeInTheDocument();
  });

  it('refreshes the elapsed clock immediately when a running row appears', async () => {
    const now = jest
      .spyOn(Date, 'now')
      .mockReturnValue(new Date('2026-08-04T10:00:00Z').getTime());
    mockListRuns.mockResolvedValueOnce([runSummary()]).mockResolvedValueOnce([
      runSummary({
        runId: 'run-live',
        status: 'running',
        success: null,
        startedAtUtc: '2026-08-04T10:00:00Z',
        updatedAtUtc: '2026-08-04T10:00:00Z',
      }),
    ]);

    try {
      renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);
      await screen.findByText('Completed');
      now.mockReturnValue(new Date('2026-08-04T10:02:00Z').getTime());

      fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

      expect(await screen.findByText('2m elapsed')).toBeInTheDocument();
    } finally {
      now.mockRestore();
    }
  });
});
