import {
  canArchiveWorkflow,
  isWorkflowArchived,
  observeWorkflowArchival,
  type WorkflowArchivalObservationItem,
} from './workflowArchival';

function workflowSummary(
  workflowId: string,
  deploymentStatus: string,
): WorkflowArchivalObservationItem {
  return {
    workflowId,
    activeRevisionId: `rev-${workflowId}`,
    deploymentId: `dep-${workflowId}`,
    deploymentStatus,
    hasCommittedSource: true,
  };
}

describe('workflow archival', () => {
  it('classifies authoritative deployment states', () => {
    expect(isWorkflowArchived({ deploymentStatus: 'Deactivated' })).toBe(true);
    expect(isWorkflowArchived({ deploymentStatus: 'deactivated' })).toBe(true);
    expect(isWorkflowArchived({ deploymentStatus: 'Active' })).toBe(false);
  });

  it('allows archival only for an active committed deployment', () => {
    expect(
      canArchiveWorkflow({
        activeRevisionId: 'rev-alpha',
        deploymentId: 'dep-alpha',
        deploymentStatus: 'Active',
        hasCommittedSource: true,
      }),
    ).toBe(true);
    expect(
      canArchiveWorkflow({
        activeRevisionId: '',
        deploymentId: '',
        deploymentStatus: '',
        hasCommittedSource: false,
      }),
    ).toBe(false);
    expect(
      canArchiveWorkflow({
        activeRevisionId: 'rev-archived',
        deploymentId: 'dep-archived',
        deploymentStatus: 'Deactivated',
        hasCommittedSource: true,
      }),
    ).toBe(false);
  });

  it('observes the exact workflow becoming deactivated', async () => {
    const wait = jest.fn(async () => undefined);
    const readWorkflows = jest
      .fn()
      .mockResolvedValueOnce([workflowSummary('wf-alpha', 'Active')])
      .mockResolvedValueOnce([workflowSummary('wf-alpha', 'Deactivated')]);

    await expect(
      observeWorkflowArchival({
        delaysMs: [0, 1],
        readWorkflows,
        wait,
        workflowId: 'wf-alpha',
      }),
    ).resolves.toEqual({
      kind: 'observed',
      workflows: [workflowSummary('wf-alpha', 'Deactivated')],
    });
    expect(readWorkflows).toHaveBeenCalledTimes(2);
    expect(wait).toHaveBeenCalledWith(1);
  });

  it('does not confuse another deactivated workflow with the target', async () => {
    const readWorkflows = jest.fn(async () => [
      workflowSummary('wf-beta', 'Deactivated'),
      workflowSummary('wf-alpha', 'Active'),
    ]);

    await expect(
      observeWorkflowArchival({
        delaysMs: [0],
        readWorkflows,
        workflowId: 'wf-alpha',
      }),
    ).resolves.toEqual({ kind: 'delayed' });
  });

  it('propagates an authoritative list failure', async () => {
    const failure = new Error('workflow list unavailable');

    await expect(
      observeWorkflowArchival({
        delaysMs: [0],
        readWorkflows: jest.fn(async () => {
          throw failure;
        }),
        workflowId: 'wf-alpha',
      }),
    ).rejects.toBe(failure);
  });
});
