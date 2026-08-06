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

const mockListRuns = jest.requireMock('@/shared/api/workflowActivityApi')
  .workflowActivityApi.listRuns as jest.Mock;

describe('Workflow Activity vNext Activity ledger', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockSearch = '';
    mockListRuns.mockResolvedValue([]);
  });

  afterEach(() => cleanupTestQueryClients());

  it('preserves the honest unavailable notice for a workflow without definition identity', async () => {
    mockSearch = '?workflowFilter=unavailable';

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(
      await screen.findByText(
        "This workflow can't be filtered yet. Showing all activity.",
      ),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(mockListRuns).toHaveBeenCalledWith('scope-alpha', {
        status: undefined,
        origins: undefined,
        definitionActorIds: undefined,
        take: 100,
      }),
    );
  });

  it('sends only URL-backed supported filters to the observatory API', async () => {
    mockSearch = '?status=failed&origin=draft&definition=definition-alpha';

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    await waitFor(() =>
      expect(mockListRuns).toHaveBeenCalledWith('scope-alpha', {
        status: 'failed',
        origins: ['draft'],
        definitionActorIds: ['definition-alpha'],
        take: 100,
      }),
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
        origins: ['draft'],
        definitionActorIds: undefined,
        take: 100,
      }),
    );
    expect(mockListRuns).not.toHaveBeenCalledWith('scope-alpha', {
      status: 'waiting',
      origins: ['draft'],
      definitionActorIds: undefined,
      take: 100,
    });
    await waitFor(() =>
      expect(history.replace).toHaveBeenLastCalledWith(
        '/scopes/scope-alpha/workflow-activity-vnext/activity?origin=draft',
      ),
    );
  });

  it('restores search from the URL without sending it to the runs API', async () => {
    mockSearch =
      '?q=customer&status=failed&origin=draft&definition=definition-alpha&workflowFilter=unavailable';

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(screen.getByRole('searchbox', { name: 'Search runs' })).toHaveValue(
      'customer',
    );
    await waitFor(() =>
      expect(mockListRuns).toHaveBeenLastCalledWith('scope-alpha', {
        status: 'failed',
        origins: ['draft'],
        definitionActorIds: ['definition-alpha'],
        take: 100,
      }),
    );

    fireEvent.change(screen.getByRole('searchbox', { name: 'Search runs' }), {
      target: { value: 'invoice' },
    });

    await waitFor(() =>
      expect(history.replace).toHaveBeenLastCalledWith(
        '/scopes/scope-alpha/workflow-activity-vnext/activity?q=invoice&status=failed&origin=draft&definition=definition-alpha&workflowFilter=unavailable',
      ),
    );
    expect(mockListRuns).toHaveBeenLastCalledWith('scope-alpha', {
      status: 'failed',
      origins: ['draft'],
      definitionActorIds: ['definition-alpha'],
      take: 100,
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
        runOrigin: 'ad-hoc-chat',
      },
    ]);

    renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

    expect(
      await screen.findByRole('button', { name: 'Open Customer follow-up' }),
    ).toBeEnabled();
    expect(screen.getByText('Completed')).toBeInTheDocument();
    expect(screen.getByText('Chat')).toBeInTheDocument();
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
});
