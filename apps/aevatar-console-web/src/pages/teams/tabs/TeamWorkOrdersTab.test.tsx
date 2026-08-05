import { fireEvent, screen, waitFor } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import React from 'react';
import { workOrdersApi } from '@/shared/api/workOrdersApi';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import TeamWorkOrdersTab from './TeamWorkOrdersTab';

jest.mock('@/shared/api/workOrdersApi', () => ({
  workOrdersApi: {
    list: jest.fn(),
  },
}));

function createWorkOrder() {
  return {
    workOrderId: 'wo-alpha',
    scopeId: 'scope-alpha',
    teamId: 'team-alpha',
    requester: { principalId: 'user-alpha', principalKind: 'user' },
    memberId: 'm-alpha',
    publishedServiceId: 'svc-alpha',
    workflowId: 'wf-alpha',
    serviceRevisionId: 'rev-alpha',
    implementationKind: 'workflow',
    endpointId: 'chat',
    intent: 'Prepare the launch brief',
    dedupKey: 'launch-brief',
    lifecycleStatus: 'ready' as const,
    lifecycleVersion: 2,
    stateVersion: 7,
    availableActions: {
      canReassign: true,
      canDispatch: true,
      canCancel: true,
    },
    input: {
      chat: { prompt: 'Prepare the launch brief' },
      inputArtifacts: [],
      declaredResultArtifacts: [],
    },
    run: { runId: 'run-alpha' },
    runOutcome: null,
    lateRunOutcome: null,
    failure: null,
    terminalReason: null,
    createdAtUtc: '2026-08-05T07:00:00Z',
    updatedAtUtc: '2026-08-05T08:00:00Z',
    timeoutAtUtc: null,
  };
}

describe('TeamWorkOrdersTab', () => {
  beforeEach(() => {
    setLocale('en-US', false);
    (workOrdersApi.list as jest.Mock).mockReset();
  });

  it('loads only the Team collection and opens the canonical WorkOrder detail', async () => {
    (workOrdersApi.list as jest.Mock).mockResolvedValue({
      scopeId: 'scope-alpha',
      workOrders: [createWorkOrder()],
      nextPageToken: null,
    });
    const onNavigate = jest.fn();

    renderWithQueryClient(
      React.createElement(TeamWorkOrdersTab, {
        onNavigate,
        scopeId: 'scope-alpha',
        teamId: 'team-alpha',
      }),
    );

    expect(await screen.findByText('Prepare the launch brief')).toBeTruthy();
    expect(screen.getByText('m-alpha')).toBeTruthy();
    expect(screen.getByText('svc-alpha')).toBeTruthy();
    expect(workOrdersApi.list).toHaveBeenCalledWith({
      scopeId: 'scope-alpha',
      teamId: 'team-alpha',
    });

    fireEvent.click(
      screen.getByRole('button', { name: 'Open request wo-alpha' }),
    );
    expect(onNavigate).toHaveBeenCalledWith(
      '/scopes/scope-alpha/teams/team-alpha/work-orders/wo-alpha',
    );
  });

  it('keeps the failed query retryable without leaving Team context', async () => {
    (workOrdersApi.list as jest.Mock)
      .mockRejectedValueOnce(new Error('read model unavailable'))
      .mockResolvedValueOnce({
        scopeId: 'scope-alpha',
        workOrders: [],
        nextPageToken: null,
      });

    renderWithQueryClient(
      React.createElement(TeamWorkOrdersTab, {
        onNavigate: jest.fn(),
        scopeId: 'scope-alpha',
        teamId: 'team-alpha',
      }),
    );

    expect(
      await screen.findByText('The Team requests could not be loaded.'),
    ).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));

    await waitFor(() => expect(workOrdersApi.list).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('No requests for this Team.')).toBeTruthy();
  });

  it('continues a Team-scoped list with the backend cursor', async () => {
    (workOrdersApi.list as jest.Mock)
      .mockResolvedValueOnce({
        scopeId: 'scope-alpha',
        workOrders: [createWorkOrder()],
        nextPageToken: 'cursor-2',
      })
      .mockResolvedValueOnce({
        scopeId: 'scope-alpha',
        workOrders: [
          {
            ...createWorkOrder(),
            workOrderId: 'wo-beta',
            intent: 'Prepare the operations brief',
          },
        ],
        nextPageToken: null,
      });

    renderWithQueryClient(
      React.createElement(TeamWorkOrdersTab, {
        onNavigate: jest.fn(),
        scopeId: 'scope-alpha',
        teamId: 'team-alpha',
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));

    expect(
      await screen.findByText('Prepare the operations brief'),
    ).toBeTruthy();
    expect(workOrdersApi.list).toHaveBeenLastCalledWith({
      scopeId: 'scope-alpha',
      teamId: 'team-alpha',
      pageToken: 'cursor-2',
    });
  });
});
