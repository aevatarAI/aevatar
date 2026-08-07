import type { ScopeServiceRevisionCatalogSnapshot } from '@/shared/models/runtime/scopeServices';
import type { ScopeWorkflowDetail } from '@/shared/models/scopes';
import type { StudioScopeBindingRevision } from '@/shared/studio/models';
import {
  observeWorkflowPublication,
  resolveWorkflowPublicationPhase,
  type WorkflowPublicationReceipt,
} from './useWorkflowPublication';

const receipt: WorkflowPublicationReceipt = {
  scopeId: 'scope-alpha',
  workflowId: 'wf-publication-alpha',
  revisionId: 'rev-publication-alpha',
  publishedServiceId: 'svc-publication-alpha',
};

function workflowDetail(
  scopeId = receipt.scopeId,
  workflowId = receipt.workflowId,
  actorId = 'actor-workflow-publication',
): ScopeWorkflowDetail & {
  readonly workflow: NonNullable<ScopeWorkflowDetail['workflow']>;
} {
  return {
    available: true,
    scopeId,
    workflow: {
      scopeId,
      workflowId,
      publishedServiceId: receipt.publishedServiceId,
      displayName: 'Publication workflow',
      serviceKey: 'workflow-publication',
      workflowName: 'Publication workflow',
      actorId,
      activeRevisionId: 'workflow-revision-alpha',
      deploymentId: 'deployment-workflow-alpha',
      deploymentStatus: 'Available',
      updatedAt: '2026-08-06T10:00:00Z',
    },
    source: null,
  };
}

function serviceRevision(
  changes: Partial<StudioScopeBindingRevision> = {},
): StudioScopeBindingRevision {
  return {
    revisionId: receipt.revisionId,
    implementationKind: 'workflow',
    status: 'Published',
    artifactHash: 'artifact-publication-alpha',
    failureReason: '',
    isDefaultServing: false,
    isActiveServing: true,
    isServingTarget: true,
    allocationWeight: 100,
    servingState: 'Active',
    deploymentId: 'deployment-service-alpha',
    primaryActorId: 'actor-service-alpha',
    createdAt: '2026-08-06T10:00:00Z',
    preparedAt: '2026-08-06T10:00:01Z',
    publishedAt: '2026-08-06T10:00:02Z',
    retiredAt: null,
    workflowName: 'Publication workflow',
    workflowDefinitionActorId: 'actor-workflow-publication',
    inlineWorkflowCount: 0,
    scriptId: '',
    scriptRevision: '',
    scriptDefinitionActorId: '',
    scriptSourceHash: '',
    staticActorTypeName: '',
    ...changes,
  };
}

function revisionCatalog(
  revisions: readonly StudioScopeBindingRevision[],
  changes: Partial<ScopeServiceRevisionCatalogSnapshot> = {},
): ScopeServiceRevisionCatalogSnapshot {
  return {
    scopeId: receipt.scopeId,
    serviceId: receipt.publishedServiceId,
    serviceKey: 'service-publication',
    displayName: 'Publication service',
    defaultServingRevisionId: receipt.revisionId,
    activeServingRevisionId: receipt.revisionId,
    deploymentId: 'deployment-service-alpha',
    deploymentStatus: 'Active',
    primaryActorId: 'actor-service-alpha',
    catalogStateVersion: 12,
    catalogLastEventId: 'evt-service-alpha',
    updatedAt: '2026-08-06T10:00:03Z',
    revisions,
    ...changes,
  };
}

function httpStatusError(
  status: number,
  code?: string,
): Error & { readonly status: number; readonly code?: string } {
  return Object.assign(new Error(`HTTP ${status}`), { status, code });
}

describe('observeWorkflowPublication', () => {
  it('observes only the accepted workflow, service, and revision once its active serving revision is published', async () => {
    let resolveWorkflow: (detail: ScopeWorkflowDetail) => void = () =>
      undefined;
    const workflowRead = jest.fn(
      () =>
        new Promise<ScopeWorkflowDetail>((resolve) => {
          resolveWorkflow = resolve;
        }),
    );
    const revisionsRead = jest
      .fn()
      .mockResolvedValue(revisionCatalog([serviceRevision()]));

    const observation = observeWorkflowPublication({
      receipt,
      readWorkflow: workflowRead,
      readRevisions: revisionsRead,
      wait: async () => undefined,
      delaysMs: [0],
    });

    await Promise.resolve();
    expect(revisionsRead).toHaveBeenCalledWith(
      'scope-alpha',
      'svc-publication-alpha',
    );
    resolveWorkflow(workflowDetail());

    await expect(observation).resolves.toMatchObject({
      kind: 'observed',
      revision: { revisionId: 'rev-publication-alpha' },
    });
    expect(workflowRead).toHaveBeenCalledWith(
      'scope-alpha',
      'wf-publication-alpha',
    );
  });

  it('recognizes case, whitespace, underscore, and hyphen variants in the published serving evidence', async () => {
    await expect(
      observeWorkflowPublication({
        receipt,
        readWorkflow: async () => workflowDetail(),
        readRevisions: async () =>
          revisionCatalog([
            serviceRevision({
              status: '  PUB_lished  ',
              servingState: ' a-c_t i-v_e ',
            }),
          ]),
        wait: async () => undefined,
        delaysMs: [0],
      }),
    ).resolves.toMatchObject({ kind: 'observed' });
  });

  it('treats receipt-bound workflow 404 and 409 plus a missing revision as a delayed observation without another publish request', async () => {
    const workflowRead = jest
      .fn()
      .mockRejectedValueOnce(httpStatusError(404))
      .mockRejectedValueOnce(httpStatusError(409, 'USER_WORKFLOW_NOT_READY'));
    const revisionsRead = jest.fn().mockResolvedValue(revisionCatalog([]));

    await expect(
      observeWorkflowPublication({
        receipt,
        readWorkflow: workflowRead,
        readRevisions: revisionsRead,
        wait: async () => undefined,
        delaysMs: [0, 0],
      }),
    ).resolves.toEqual({ kind: 'delayed' });
    expect(workflowRead.mock.calls).toEqual([
      ['scope-alpha', 'wf-publication-alpha'],
      ['scope-alpha', 'wf-publication-alpha'],
    ]);
    expect(revisionsRead.mock.calls).toEqual([
      ['scope-alpha', 'svc-publication-alpha'],
      ['scope-alpha', 'svc-publication-alpha'],
    ]);
  });

  it('treats a receipt-bound service revision catalog 404 as observation delay', async () => {
    const revisionsRead = jest.fn().mockRejectedValue(httpStatusError(404));

    await expect(
      observeWorkflowPublication({
        receipt,
        readWorkflow: async () => workflowDetail(),
        readRevisions: revisionsRead,
        wait: async () => undefined,
        delaysMs: [0],
      }),
    ).resolves.toEqual({ kind: 'delayed' });
    expect(revisionsRead).toHaveBeenCalledWith(
      'scope-alpha',
      'svc-publication-alpha',
    );
  });

  it('delays only the recognized workflow projection conflict codes', async () => {
    for (const code of ['USER_WORKFLOW_NOT_READY', 'USER_WORKFLOW_STALE']) {
      await expect(
        observeWorkflowPublication({
          receipt,
          readWorkflow: async () => {
            throw httpStatusError(409, code);
          },
          readRevisions: async () => revisionCatalog([serviceRevision()]),
          wait: async () => undefined,
          delaysMs: [0],
        }),
      ).resolves.toEqual({ kind: 'delayed' });
    }
  });

  it('fails instead of retrying bare and unrelated workflow conflicts', async () => {
    for (const error of [
      httpStatusError(409),
      httpStatusError(409, 'USER_WORKFLOW_CONFLICT'),
    ]) {
      await expect(
        observeWorkflowPublication({
          receipt,
          readWorkflow: async () => {
            throw error;
          },
          readRevisions: async () => revisionCatalog([serviceRevision()]),
          wait: async () => undefined,
          delaysMs: [0, 0],
        }),
      ).rejects.toBe(error);
    }
  });

  it('fails fulfilled workflow and catalog identity mismatches', async () => {
    const acceptedWorkflow = workflowDetail();
    const cases = [
      {
        workflow: workflowDetail('scope-other', 'wf-other'),
        catalog: revisionCatalog([serviceRevision()]),
      },
      {
        workflow: {
          ...acceptedWorkflow,
          workflow: {
            ...acceptedWorkflow.workflow,
            scopeId: 'scope-other',
          },
        },
        catalog: revisionCatalog([serviceRevision()]),
      },
      {
        workflow: workflowDetail(),
        catalog: revisionCatalog([serviceRevision()], {
          scopeId: 'scope-other',
          serviceId: 'svc-other',
        }),
      },
    ];

    for (const candidate of cases) {
      const workflowRead = jest.fn().mockResolvedValue(candidate.workflow);
      const revisionsRead = jest.fn().mockResolvedValue(candidate.catalog);

      await expect(
        observeWorkflowPublication({
          receipt,
          readWorkflow: workflowRead,
          readRevisions: revisionsRead,
          wait: async () => undefined,
          delaysMs: [0, 0],
        }),
      ).rejects.toThrow('does not match');
      expect(workflowRead).toHaveBeenCalledTimes(1);
      expect(revisionsRead).toHaveBeenCalledTimes(1);
    }
  });

  it('keeps observing when the exact revision exists but a prior revision remains active', async () => {
    const workflowRead = jest.fn().mockResolvedValue(workflowDetail());
    const revisionsRead = jest.fn().mockResolvedValue(
      revisionCatalog([serviceRevision()], {
        activeServingRevisionId: 'rev-prior-alpha',
      }),
    );

    await expect(
      observeWorkflowPublication({
        receipt,
        readWorkflow: workflowRead,
        readRevisions: revisionsRead,
        wait: async () => undefined,
        delaysMs: [0],
      }),
    ).resolves.toEqual({ kind: 'delayed' });
    expect(workflowRead).toHaveBeenCalledTimes(1);
    expect(revisionsRead).toHaveBeenCalledTimes(1);
  });

  it('fails when the exact serving revision implements a different workflow definition', async () => {
    const workflowRead = jest
      .fn()
      .mockResolvedValue(
        workflowDetail(
          receipt.scopeId,
          receipt.workflowId,
          'actor-workflow-other',
        ),
      );
    const revisionsRead = jest
      .fn()
      .mockResolvedValue(revisionCatalog([serviceRevision()]));

    await expect(
      observeWorkflowPublication({
        receipt,
        readWorkflow: workflowRead,
        readRevisions: revisionsRead,
        wait: async () => undefined,
        delaysMs: [0, 0],
      }),
    ).rejects.toThrow('does not implement');
    expect(workflowRead).toHaveBeenCalledTimes(1);
    expect(revisionsRead).toHaveBeenCalledTimes(1);
  });

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

  it.each([
    ['PreparationFailed', serviceRevision({ status: 'preparation_failed' })],
    ['failure reason', serviceRevision({ failureReason: 'artifact rejected' })],
    ['Retired', serviceRevision({ status: ' RETIRED ' })],
  ])('stops when the accepted revision reaches terminal %s state', async (_, revision) => {
    const workflowRead = jest
      .fn()
      .mockRejectedValue(httpStatusError(409, 'USER_WORKFLOW_NOT_READY'));
    const revisionsRead = jest
      .fn()
      .mockResolvedValue(revisionCatalog([revision]));

    await expect(
      observeWorkflowPublication({
        receipt,
        readWorkflow: workflowRead,
        readRevisions: revisionsRead,
        wait: async () => undefined,
        delaysMs: [0, 0],
      }),
    ).rejects.toThrow('terminal');
    expect(workflowRead).toHaveBeenCalledTimes(1);
    expect(revisionsRead).toHaveBeenCalledTimes(1);
  });
});
