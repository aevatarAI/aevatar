import type { PlaygroundDraft } from './playgroundDraft';
import { getPlaygroundDraftStatus, summarizeYamlLineDiff } from './draftStatus';

function createDraft(overrides?: Partial<PlaygroundDraft>): PlaygroundDraft {
  return {
    yaml: 'name: sample\nsteps: []\n',
    prompt: 'run this',
    sourceWorkflow: 'direct_review',
    updatedAt: '2026-03-12T00:00:00Z',
    ...overrides,
  };
}

describe('getPlaygroundDraftStatus', () => {
  it('reports when no draft exists', () => {
    expect(
      getPlaygroundDraftStatus(
        createDraft({
          yaml: '',
          prompt: '',
          sourceWorkflow: '',
          updatedAt: '',
        }),
      ),
    ).toMatchObject({
      hasDraft: false,
      label: 'No draft saved',
      summary: 'No draft saved',
      alertType: 'info',
    });
  });

  it('reports when the current draft still matches the selected workflow', () => {
    expect(
      getPlaygroundDraftStatus(createDraft(), {
        referenceWorkflow: 'direct_review',
        referenceYaml: 'name: sample\nsteps: []\n',
      }),
    ).toMatchObject({
      hasDraft: true,
      label: 'Aligned with template',
      summary: 'Matches direct_review',
      alertType: 'success',
      differsFromReference: false,
    });
  });

  it('reports local edits when the draft diverges from the selected workflow', () => {
    expect(
      getPlaygroundDraftStatus(createDraft(), {
        referenceWorkflow: 'direct_review',
        referenceYaml: 'name: sample\nsteps:\n  - id: start\n',
      }),
    ).toMatchObject({
      hasDraft: true,
      label: 'Modified from template',
      summary: 'Edited from direct_review',
      alertType: 'warning',
      differsFromReference: true,
    });
  });

  it('reports when the draft is based on another workflow', () => {
    expect(
      getPlaygroundDraftStatus(createDraft(), {
        referenceWorkflow: 'human_input_manual_triage',
        referenceYaml: 'name: human_input_manual_triage\nsteps: []\n',
      }),
    ).toMatchObject({
      hasDraft: true,
      label: 'Linked to another workflow',
      summary: 'Based on direct_review',
      alertType: 'info',
      matchesReferenceWorkflow: false,
    });
  });

  it('summarizes line-level draft differences against a template', () => {
    expect(
      summarizeYamlLineDiff(
        'name: draft\nsteps:\n  - id: start\n  - id: finish\n',
        'name: draft\nsteps:\n  - id: start\n',
      ),
    ).toEqual({
      draftLineCount: 4,
      referenceLineCount: 3,
      changedLineCount: 1,
      addedLineCount: 1,
      removedLineCount: 0,
    });
  });
});
