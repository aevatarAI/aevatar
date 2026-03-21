import {
  defaultPlaygroundDraft,
  loadPlaygroundDraft,
  resetPlaygroundDraft,
  savePlaygroundDraft,
} from './playgroundDraft';

describe('playgroundDraft', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it('loads defaults when storage is empty and persists sanitized values', () => {
    expect(loadPlaygroundDraft()).toEqual(defaultPlaygroundDraft);

    const saved = savePlaygroundDraft({
      yaml: 'name: draft-workflow\nsteps: []\n',
      prompt: '  summarize the draft  ',
      sourceWorkflow: '  direct  ',
    });

    expect(saved).toEqual({
      yaml: 'name: draft-workflow\nsteps: []\n',
      prompt: 'summarize the draft',
      sourceWorkflow: 'direct',
      updatedAt: expect.any(String),
    });

    expect(loadPlaygroundDraft()).toEqual(saved);
    expect(resetPlaygroundDraft()).toEqual(defaultPlaygroundDraft);
    expect(loadPlaygroundDraft()).toEqual(defaultPlaygroundDraft);
  });
});
