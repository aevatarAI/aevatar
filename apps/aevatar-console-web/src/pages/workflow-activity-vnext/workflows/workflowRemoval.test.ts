import { observeWorkflowRemoval } from './workflowRemoval';

describe('workflow removal observation', () => {
  it('observes the exact workflow disappearing from the authoritative catalogue', async () => {
    const readWorkflows = jest
      .fn()
      .mockResolvedValueOnce([{ workflowId: 'wf-alpha' }])
      .mockResolvedValueOnce([]);

    await expect(
      observeWorkflowRemoval({
        delaysMs: [0, 1],
        readWorkflows,
        wait: async () => undefined,
        workflowId: 'wf-alpha',
      }),
    ).resolves.toEqual({ kind: 'observed' });
    expect(readWorkflows).toHaveBeenCalledTimes(2);
  });

  it('does not confuse another workflow disappearing with the target', async () => {
    const readWorkflows = jest.fn(async () => [
      { workflowId: 'wf-alpha' },
      { workflowId: 'wf-beta' },
    ]);

    await expect(
      observeWorkflowRemoval({
        delaysMs: [0],
        readWorkflows,
        wait: async () => undefined,
        workflowId: 'wf-alpha',
      }),
    ).resolves.toEqual({ kind: 'delayed' });
  });

  it('returns delayed after the bounded observation window', async () => {
    const readWorkflows = jest.fn(async () => [{ workflowId: 'wf-alpha' }]);

    await expect(
      observeWorkflowRemoval({
        delaysMs: [0, 0],
        readWorkflows,
        wait: async () => undefined,
        workflowId: 'wf-alpha',
      }),
    ).resolves.toEqual({ kind: 'delayed' });
    expect(readWorkflows).toHaveBeenCalledTimes(2);
  });
});
