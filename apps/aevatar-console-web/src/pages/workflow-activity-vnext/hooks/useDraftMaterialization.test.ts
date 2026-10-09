import { studioApi } from '@/shared/studio/api';
import {
  observeDraftMaterialization,
  readWorkflowDraftAfterList,
} from './useDraftMaterialization';

describe('observeDraftMaterialization', () => {
  it('treats bounded 404 as projection delay and keeps observing the exact receipt id', async () => {
    const pending = Object.assign(new Error('pending'), { status: 404 });
    const read = jest
      .fn()
      .mockRejectedValueOnce(pending)
      .mockRejectedValueOnce(pending)
      .mockResolvedValueOnce({ workflowId: 'wf-api-returned' });

    await expect(
      observeDraftMaterialization({
        workflowId: 'wf-api-returned',
        read,
        isNotFound: (error) => error === pending,
        wait: async () => undefined,
        delaysMs: [0, 0, 0],
      }),
    ).resolves.toEqual({
      kind: 'readable',
      workflow: { workflowId: 'wf-api-returned' },
    });
    expect(read.mock.calls).toEqual([
      ['wf-api-returned'],
      ['wf-api-returned'],
      ['wf-api-returned'],
    ]);
  });

  it('returns delayed without inventing success and retry reads the same id', async () => {
    const pending = Object.assign(new Error('pending'), { status: 404 });
    const read = jest.fn().mockRejectedValue(pending);
    const input = {
      workflowId: 'wf-api-returned',
      read,
      isNotFound: (error: unknown) => error === pending,
      wait: async () => undefined,
      delaysMs: [0, 0],
    };

    await expect(observeDraftMaterialization(input)).resolves.toEqual({
      kind: 'delayed',
    });
    await expect(observeDraftMaterialization(input)).resolves.toEqual({
      kind: 'delayed',
    });
    expect(read).toHaveBeenCalledTimes(4);
    expect(new Set(read.mock.calls.map(([workflowId]) => workflowId))).toEqual(
      new Set(['wf-api-returned']),
    );
  });

  it('keeps observing an existing draft until the expected update is visible', async () => {
    const read = jest
      .fn<Promise<{ workflowId: string; name: string }>, [workflowId: string]>()
      .mockResolvedValueOnce({ workflowId: 'wf-alpha', name: 'Old name' })
      .mockResolvedValueOnce({ workflowId: 'wf-alpha', name: 'New name' });

    await expect(
      observeDraftMaterialization({
        workflowId: 'wf-alpha',
        read,
        isNotFound: () => false,
        isObserved: (workflow) => workflow.name === 'New name',
        wait: async () => undefined,
        delaysMs: [0, 0],
      }),
    ).resolves.toEqual({
      kind: 'readable',
      workflow: { workflowId: 'wf-alpha', name: 'New name' },
    });
    expect(read).toHaveBeenCalledTimes(2);
  });

  it('waits for the draft list before requesting the draft document', async () => {
    const listWorkflowDrafts = jest
      .spyOn(studioApi, 'listWorkflowDrafts')
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([
        {
          workflowId: 'wf-template',
          name: 'Template workflow',
          description: '',
          fileName: 'template-workflow.yaml',
          filePath: 'scope/template-workflow.yaml',
          directoryId: 'scope:scope-1',
          directoryLabel: 'scope-1',
          stepCount: 1,
          hasLayout: false,
          updatedAtUtc: '2026-08-20T00:00:00.000Z',
        },
      ]);
    const getWorkflowDraftFile = jest
      .spyOn(studioApi, 'getWorkflowDraftFile')
      .mockResolvedValue({
        workflowId: 'wf-template',
        name: 'Template workflow',
        fileName: 'template-workflow.yaml',
        filePath: 'scope/template-workflow.yaml',
        directoryId: 'scope:scope-1',
        directoryLabel: 'scope-1',
        yaml: 'name: template-workflow\n',
        layout: null,
        updatedAtUtc: '2026-08-20T00:00:00.000Z',
        findings: [],
      });

    await expect(
      readWorkflowDraftAfterList('wf-template', 'scope-1'),
    ).resolves.toBeNull();
    await expect(
      readWorkflowDraftAfterList('wf-template', 'scope-1'),
    ).resolves.toMatchObject({ workflowId: 'wf-template' });

    expect(listWorkflowDrafts).toHaveBeenCalledWith('scope-1');
    expect(getWorkflowDraftFile).toHaveBeenCalledWith('wf-template', 'scope-1');

    listWorkflowDrafts.mockRestore();
    getWorkflowDraftFile.mockRestore();
  });

  it('surfaces non-404 failures immediately', async () => {
    const forbidden = Object.assign(new Error('forbidden'), { status: 403 });
    const read = jest.fn().mockRejectedValue(forbidden);

    await expect(
      observeDraftMaterialization({
        workflowId: 'wf-api-returned',
        read,
        isNotFound: () => false,
        wait: async () => undefined,
        delaysMs: [0, 0],
      }),
    ).rejects.toBe(forbidden);
    expect(read).toHaveBeenCalledTimes(1);
  });
});
