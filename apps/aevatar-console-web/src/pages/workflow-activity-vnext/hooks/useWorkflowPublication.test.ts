import type { ScopeWorkflowDetail } from '@/shared/models/scopes';
import {
  observeWorkflowPublication,
  resolveWorkflowPublicationPhase,
  type WorkflowPublicationReceipt,
} from './useWorkflowPublication';

const receipt: WorkflowPublicationReceipt = {
  scopeId: 'scope-alpha',
  workflowId: 'wf-publication-alpha',
  revisionId: 'rev-publication-alpha',
};

function workflowDetail(
  changes: Partial<NonNullable<ScopeWorkflowDetail['workflow']>> = {},
  detailChanges: Partial<Omit<ScopeWorkflowDetail, 'workflow'>> = {},
): ScopeWorkflowDetail & {
  readonly workflow: NonNullable<ScopeWorkflowDetail['workflow']>;
} {
  return {
    available: true,
    scopeId: receipt.scopeId,
    workflow: {
      scopeId: receipt.scopeId,
      workflowId: receipt.workflowId,
      displayName: 'Publication workflow',
      serviceKey: 'workflow-publication',
      workflowName: 'Publication workflow',
      actorId: 'actor-workflow-publication',
      activeRevisionId: receipt.revisionId,
      publishedServiceId: 'svc-publication-alpha',
      serviceAppId: 'workflow-app',
      serviceNamespace: 'workflow-namespace',
      deploymentId: 'deployment-workflow-alpha',
      deploymentStatus: 'Available',
      updatedAt: '2026-08-06T10:00:00Z',
      ...changes,
    },
    source: null,
    ...detailChanges,
  };
}

function httpStatusError(
  status: number,
  code?: string,
): Error & { readonly status: number; readonly code?: string } {
  return Object.assign(new Error(`HTTP ${status}`), { status, code });
}

describe('observeWorkflowPublication', () => {
  it('observes the accepted workflow from its read model without querying a service revision catalog', async () => {
    const workflowRead = jest.fn().mockResolvedValue(workflowDetail());

    await expect(
      observeWorkflowPublication({
        receipt,
        readWorkflow: workflowRead,
        wait: async () => undefined,
        delaysMs: [0],
      }),
    ).resolves.toMatchObject({
      kind: 'observed',
      publishedServiceId: 'svc-publication-alpha',
      workflow: {
        workflow: {
          activeRevisionId: 'rev-publication-alpha',
          publishedServiceId: 'svc-publication-alpha',
        },
      },
    });

    expect(workflowRead).toHaveBeenCalledWith(
      'scope-alpha',
      'wf-publication-alpha',
    );
    expect(workflowRead).toHaveBeenCalledTimes(1);
  });

  it('delays while the workflow read model has not reached the accepted revision', async () => {
    const workflowRead = jest
      .fn()
      .mockResolvedValue(
        workflowDetail({ activeRevisionId: 'rev-prior-alpha' }),
      );

    await expect(
      observeWorkflowPublication({
        receipt,
        readWorkflow: workflowRead,
        wait: async () => undefined,
        delaysMs: [0, 0],
      }),
    ).resolves.toEqual({ kind: 'delayed' });

    expect(workflowRead.mock.calls).toEqual([
      ['scope-alpha', 'wf-publication-alpha'],
      ['scope-alpha', 'wf-publication-alpha'],
    ]);
  });

  it('treats expected workflow materialization and transient transport responses as delayed observation', async () => {
    for (const error of [
      httpStatusError(404),
      httpStatusError(408),
      httpStatusError(409, 'USER_WORKFLOW_NOT_READY'),
      httpStatusError(409, 'USER_WORKFLOW_STALE'),
      httpStatusError(429),
      httpStatusError(503),
    ]) {
      await expect(
        observeWorkflowPublication({
          receipt,
          readWorkflow: async () => {
            throw error;
          },
          wait: async () => undefined,
          delaysMs: [0],
        }),
      ).resolves.toEqual({ kind: 'delayed' });
    }
  });

  it('fails fulfilled workflow identity mismatches instead of accepting a different resource', async () => {
    const mismatches = [
      workflowDetail({}, { scopeId: 'scope-other' }),
      workflowDetail({ scopeId: 'scope-other' }),
      workflowDetail({ workflowId: 'wf-other' }),
    ];

    for (const workflow of mismatches) {
      await expect(
        observeWorkflowPublication({
          receipt,
          readWorkflow: async () => workflow,
          wait: async () => undefined,
          delaysMs: [0],
        }),
      ).rejects.toThrow('does not match');
    }
  });

  it.each([
    ['empty', ''],
    ['blank', '  '],
    ['legacy default', 'default'],
  ])('keeps observation pending for a %s published service identity', async (_caseName, publishedServiceId) => {
    await expect(
      observeWorkflowPublication({
        receipt,
        readWorkflow: async () => workflowDetail({ publishedServiceId }),
        wait: async () => undefined,
        delaysMs: [0],
      }),
    ).resolves.toEqual({ kind: 'delayed' });
  });
});

describe('resolveWorkflowPublicationPhase', () => {
  it('maps authorization errors from an exact observation read to distinct phases', () => {
    expect(
      resolveWorkflowPublicationPhase({
        data: undefined,
        enabled: true,
        error: httpStatusError(401),
        isFetching: false,
        isPending: false,
      }),
    ).toBe('unauthorized');
    expect(
      resolveWorkflowPublicationPhase({
        data: undefined,
        enabled: true,
        error: httpStatusError(403),
        isFetching: false,
        isPending: false,
      }),
    ).toBe('forbidden');
  });
});
