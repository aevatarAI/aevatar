import { authFetch } from '@/shared/auth/fetch';
import { workOrdersApi } from './workOrdersApi';

jest.mock('@/shared/auth/fetch', () => ({
  authFetch: jest.fn(),
}));

const mockedAuthFetch = authFetch as jest.Mock;

function createStatePayload() {
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
  };
}

function okJson(payload: unknown): Response {
  return {
    ok: true,
    json: async () => payload,
  } as Response;
}

describe('workOrdersApi', () => {
  beforeEach(() => {
    mockedAuthFetch.mockReset();
  });

  it('lists Team-scoped WorkOrders without collapsing resource identities', async () => {
    mockedAuthFetch.mockResolvedValueOnce(
      okJson({
        scopeId: 'scope-alpha',
        workOrders: [createStatePayload()],
        nextPageToken: null,
      }),
    );

    const result = await workOrdersApi.list({
      scopeId: 'scope-alpha',
      teamId: 'team-alpha',
    });

    expect(mockedAuthFetch).toHaveBeenCalledWith(
      '/api/scopes/scope-alpha/work-orders?teamId=team-alpha&pageSize=200',
      undefined,
    );
    expect(result.workOrders[0]).toMatchObject({
      workOrderId: 'wo-alpha',
      memberId: 'm-alpha',
      workflowId: 'wf-alpha',
      publishedServiceId: 'svc-alpha',
      run: { runId: 'run-alpha' },
      availableActions: { canDispatch: true },
    });
  });

  it('sends Actor-observed lifecycle versions with each management command', async () => {
    mockedAuthFetch.mockResolvedValue(
      okJson({
        workOrderId: 'wo-alpha',
        commandId: 'cmd-alpha',
        correlationId: 'corr-alpha',
        stage: 'dispatch_accepted',
        acceptedAtUtc: null,
      }),
    );

    await workOrdersApi.reassign({
      scopeId: 'scope-alpha',
      workOrderId: 'wo-alpha',
      memberId: 'm-beta',
      publishedServiceId: 'svc-beta',
      expectedLifecycleVersion: 7,
    });
    await workOrdersApi.dispatch({
      scopeId: 'scope-alpha',
      workOrderId: 'wo-alpha',
      expectedLifecycleVersion: 8,
    });
    await workOrdersApi.cancel({
      scopeId: 'scope-alpha',
      workOrderId: 'wo-alpha',
      expectedLifecycleVersion: 9,
      reason: 'No longer needed',
    });

    expect(mockedAuthFetch.mock.calls.map(([path]) => path)).toEqual([
      '/api/scopes/scope-alpha/work-orders/wo-alpha:reassign',
      '/api/scopes/scope-alpha/work-orders/wo-alpha:dispatch',
      '/api/scopes/scope-alpha/work-orders/wo-alpha:cancel',
    ]);
    expect(JSON.parse(mockedAuthFetch.mock.calls[0][1].body)).toEqual({
      memberId: 'm-beta',
      publishedServiceId: 'svc-beta',
      expectedLifecycleVersion: 7,
    });
    expect(JSON.parse(mockedAuthFetch.mock.calls[1][1].body)).toEqual({
      expectedLifecycleVersion: 8,
    });
    expect(JSON.parse(mockedAuthFetch.mock.calls[2][1].body)).toEqual({
      expectedLifecycleVersion: 9,
      reason: 'No longer needed',
    });
  });

  it('preserves typed backend error details for fail-closed recovery', async () => {
    mockedAuthFetch.mockResolvedValueOnce({
      ok: false,
      status: 400,
      statusText: 'Bad Request',
      text: async () =>
        JSON.stringify({
          code: 'INVALID_WORK_ORDER_COMMAND',
          message: 'The command was rejected.',
        }),
    } as Response);

    await expect(
      workOrdersApi.dispatch({
        scopeId: 'scope-alpha',
        workOrderId: 'wo-alpha',
        expectedLifecycleVersion: 2,
      }),
    ).rejects.toMatchObject({
      name: 'WorkOrderApiError',
      status: 400,
      code: 'INVALID_WORK_ORDER_COMMAND',
    });
  });
});
