import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import React from 'react';
import { workOrdersApi } from '@/shared/api/workOrdersApi';
import { studioApi } from '@/shared/studio/api';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import TeamWorkOrderDetailPage from './index';

jest.mock('@/shared/api/workOrdersApi', () => ({
  workOrdersApi: {
    cancel: jest.fn(),
    dispatch: jest.fn(),
    get: jest.fn(),
    reassign: jest.fn(),
  },
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn(),
    listTeamMembers: jest.fn(),
  },
}));

function createWorkOrder(overrides: Record<string, unknown> = {}) {
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
    lifecycleStatus: 'ready',
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
    run: {
      runId: 'run-alpha',
      runActorId: 'actor-run-alpha',
      commandId: 'cmd-alpha',
      correlationId: 'corr-alpha',
      revisionId: 'rev-alpha',
      deploymentId: 'dep-alpha',
      acceptedAtUtc: '2026-08-05T08:00:00Z',
    },
    runOutcome: null,
    lateRunOutcome: null,
    failure: null,
    terminalReason: null,
    createdAtUtc: '2026-08-05T07:00:00Z',
    updatedAtUtc: '2026-08-05T08:00:00Z',
    timeoutAtUtc: null,
    ...overrides,
  };
}

function createAuthSession(subject: string) {
  return {
    enabled: true,
    authenticated: true,
    profile: {
      subject,
      roles: [],
      groups: [],
    },
    session: null,
  };
}

describe('TeamWorkOrderDetailPage', () => {
  beforeEach(() => {
    setLocale('en-US', false);
    window.history.replaceState(
      {},
      '',
      '/scopes/scope-alpha/teams/team-alpha/work-orders/wo-alpha',
    );
    (workOrdersApi.get as jest.Mock).mockReset();
    (workOrdersApi.get as jest.Mock).mockResolvedValue(createWorkOrder());
    (workOrdersApi.reassign as jest.Mock).mockReset();
    (workOrdersApi.dispatch as jest.Mock).mockReset();
    (workOrdersApi.cancel as jest.Mock).mockReset();
    (studioApi.getAuthSession as jest.Mock).mockReset();
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue(
      createAuthSession('user-alpha'),
    );
    (studioApi.listTeamMembers as jest.Mock).mockReset();
    (studioApi.listTeamMembers as jest.Mock).mockResolvedValue({
      scopeId: 'scope-alpha',
      members: [
        {
          memberId: 'm-alpha',
          publishedServiceId: 'svc-alpha',
          displayName: 'Member Alpha',
        },
        {
          memberId: 'm-beta',
          publishedServiceId: 'svc-beta',
          displayName: 'Member Beta',
        },
      ],
      nextPageToken: null,
    });
  });

  it('uses typed identities and the observed lifecycle version when reassigning', async () => {
    (workOrdersApi.reassign as jest.Mock).mockResolvedValue({
      workOrderId: 'wo-alpha',
      commandId: 'cmd-reassign',
      correlationId: 'corr-reassign',
      stage: 'dispatch_accepted',
      acceptedAtUtc: null,
    });

    renderWithQueryClient(React.createElement(TeamWorkOrderDetailPage));

    expect(
      (await screen.findAllByText('Prepare the launch brief')).length,
    ).toBeGreaterThan(0);
    expect(screen.getByText('wf-alpha')).toBeTruthy();
    expect(screen.getAllByText('run-alpha').length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole('button', { name: 'Reassign' }));

    const dialog = await screen.findByRole('dialog', {
      name: 'Reassign request',
    });
    fireEvent.mouseDown(within(dialog).getByLabelText('Member'));
    fireEvent.click(await screen.findByText('Member Beta · svc-beta'));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Reassign' }));

    await waitFor(() =>
      expect(workOrdersApi.reassign).toHaveBeenCalledWith({
        scopeId: 'scope-alpha',
        workOrderId: 'wo-alpha',
        memberId: 'm-beta',
        publishedServiceId: 'svc-beta',
        expectedLifecycleVersion: 2,
      }),
    );
    expect(
      await screen.findByText('Awaiting read-model observation'),
    ).toBeTruthy();
  });

  it('does not expose management actions to a different authenticated principal', async () => {
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue(
      createAuthSession('user-beta'),
    );

    renderWithQueryClient(React.createElement(TeamWorkOrderDetailPage));

    expect(
      await screen.findByText(
        'Only the original requester can manage this request.',
      ),
    ).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Reassign' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Dispatch' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Cancel request' })).toBeNull();
  });

  it('refreshes the current-state read model after a rejected command', async () => {
    (workOrdersApi.dispatch as jest.Mock).mockRejectedValue(
      new Error('The command was rejected.'),
    );

    renderWithQueryClient(React.createElement(TeamWorkOrderDetailPage));

    fireEvent.click(await screen.findByRole('button', { name: 'Dispatch' }));
    const dialog = await screen.findByRole('dialog', {
      name: 'Dispatch request',
    });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Dispatch' }));

    await waitFor(() =>
      expect(workOrdersApi.dispatch).toHaveBeenCalledTimes(1),
    );
    await waitFor(() => expect(workOrdersApi.get).toHaveBeenCalledTimes(2));
    expect(workOrdersApi.dispatch).toHaveBeenCalledWith({
      scopeId: 'scope-alpha',
      workOrderId: 'wo-alpha',
      expectedLifecycleVersion: 2,
    });
  });
});
