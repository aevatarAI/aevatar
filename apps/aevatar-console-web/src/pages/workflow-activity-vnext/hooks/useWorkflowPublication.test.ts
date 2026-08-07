import type { StudioMemberBindingRunStatusResponse } from '@/shared/studio/models';
import {
  observeWorkflowPublication,
  resolveWorkflowPublicationPhase,
  type WorkflowPublicationReceipt,
} from './useWorkflowPublication';

const receipt: WorkflowPublicationReceipt = {
  scopeId: 'scope-alpha',
  workflowId: 'wf-alpha',
  memberId: 'm-alpha',
  bindingRunId: 'bind-alpha',
  revisionId: 'rev-alpha',
};

function bindingRun(
  changes: Partial<StudioMemberBindingRunStatusResponse> = {},
): StudioMemberBindingRunStatusResponse {
  return {
    status: 'accepted',
    bindingRunId: receipt.bindingRunId,
    scopeId: receipt.scopeId,
    memberId: receipt.memberId,
    stateVersion: 7,
    platformBindingCommandId: 'command-alpha',
    result: null,
    failure: null,
    updatedAt: '2026-08-07T10:00:00Z',
    ...changes,
  };
}

function httpStatusError(status: number): Error & { readonly status: number } {
  return Object.assign(new Error(`HTTP ${status}`), { status });
}

describe('observeWorkflowPublication', () => {
  it.each([
    'accepted',
    'admission_pending',
    'admitted',
    'platform_binding_pending',
    'member_notification_pending',
  ] as const)('keeps observing the accepted binding run while it is %s', async (status) => {
    const readBindingRun = jest.fn().mockResolvedValue(bindingRun({ status }));

    await expect(
      observeWorkflowPublication({
        receipt,
        readBindingRun,
        wait: async () => undefined,
        delaysMs: [0],
      }),
    ).resolves.toEqual({ kind: 'delayed' });
    expect(readBindingRun).toHaveBeenCalledWith(
      'scope-alpha',
      'm-alpha',
      'bind-alpha',
    );
  });

  it('observes publishedServiceId only from a matching succeeded binding run', async () => {
    await expect(
      observeWorkflowPublication({
        receipt,
        readBindingRun: async () =>
          bindingRun({
            status: 'succeeded',
            result: {
              publishedServiceId: 'svc-alpha',
              revisionId: 'rev-alpha',
              implementationKind: 'workflow',
              expectedActorId: 'actor-alpha',
            },
          }),
        wait: async () => undefined,
        delaysMs: [0],
      }),
    ).resolves.toEqual({
      kind: 'observed',
      publishedServiceId: 'svc-alpha',
      run: expect.objectContaining({
        status: 'succeeded',
        bindingRunId: 'bind-alpha',
      }),
    });
  });

  it('treats binding-run projection 404 as delayed observation without resubmission', async () => {
    const readBindingRun = jest.fn().mockRejectedValue(httpStatusError(404));

    await expect(
      observeWorkflowPublication({
        receipt,
        readBindingRun,
        wait: async () => undefined,
        delaysMs: [0, 0],
      }),
    ).resolves.toEqual({ kind: 'delayed' });
    expect(readBindingRun).toHaveBeenCalledTimes(2);
  });

  it.each([
    ['scope', { scopeId: 'scope-other' }],
    ['member', { memberId: 'm-other' }],
    ['run', { bindingRunId: 'bind-other' }],
  ])('rejects a binding run with mismatched %s identity', async (_, changes) => {
    await expect(
      observeWorkflowPublication({
        receipt,
        readBindingRun: async () => bindingRun(changes),
        wait: async () => undefined,
        delaysMs: [0],
      }),
    ).rejects.toThrow('does not match');
  });

  it('rejects a succeeded run for a different revision', async () => {
    await expect(
      observeWorkflowPublication({
        receipt,
        readBindingRun: async () =>
          bindingRun({
            status: 'succeeded',
            result: {
              publishedServiceId: 'svc-alpha',
              revisionId: 'rev-other',
              implementationKind: 'workflow',
            },
          }),
        wait: async () => undefined,
        delaysMs: [0],
      }),
    ).rejects.toThrow('revision');
  });

  it.each([
    'failed',
    'rejected',
  ] as const)('stops when the accepted binding run is %s', async (status) => {
    await expect(
      observeWorkflowPublication({
        receipt,
        readBindingRun: async () =>
          bindingRun({
            status,
            failure: {
              code: 'BINDING_FAILED',
              message: 'Platform binding failed.',
            },
          }),
        wait: async () => undefined,
        delaysMs: [0, 0],
      }),
    ).rejects.toThrow('Platform binding failed.');
  });

  it('fails a malformed succeeded response instead of guessing a service identity', async () => {
    await expect(
      observeWorkflowPublication({
        receipt,
        readBindingRun: async () => bindingRun({ status: 'succeeded' }),
        wait: async () => undefined,
        delaysMs: [0],
      }),
    ).rejects.toThrow('published service');
  });

  it('maps authorization errors from the binding-run read to distinct phases', () => {
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
