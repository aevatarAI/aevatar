import {
  clearPlaygroundPromptHistory,
  loadPlaygroundPromptHistory,
  savePlaygroundPromptHistoryEntry,
} from './promptHistory';

describe('playground prompt history', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it('stores trimmed prompts and keeps latest entries first', () => {
    expect(loadPlaygroundPromptHistory()).toEqual([]);

    savePlaygroundPromptHistoryEntry({
      prompt: '  Review this draft  ',
      workflowName: 'direct_review',
    });
    const entries = savePlaygroundPromptHistoryEntry({
      prompt: 'Summarize the current workflow',
      workflowName: 'direct_review',
    });

    expect(entries).toHaveLength(2);
    expect(entries[0]).toMatchObject({
      prompt: 'Summarize the current workflow',
      workflowName: 'direct_review',
    });
    expect(entries[1]).toMatchObject({
      prompt: 'Review this draft',
      workflowName: 'direct_review',
    });
  });

  it('deduplicates identical prompt and workflow pairs', () => {
    savePlaygroundPromptHistoryEntry({
      prompt: 'Review this draft',
      workflowName: 'direct_review',
    });
    const entries = savePlaygroundPromptHistoryEntry({
      prompt: 'Review this draft',
      workflowName: 'direct_review',
    });

    expect(entries).toHaveLength(1);
  });

  it('clears stored history', () => {
    savePlaygroundPromptHistoryEntry({
      prompt: 'Review this draft',
      workflowName: 'direct_review',
    });

    expect(clearPlaygroundPromptHistory()).toEqual([]);
    expect(loadPlaygroundPromptHistory()).toEqual([]);
  });
});
