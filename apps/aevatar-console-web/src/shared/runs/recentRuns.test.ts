import {
  clearRecentRuns,
  loadRecentRuns,
  saveRecentRun,
} from './recentRuns';

describe('recentRuns', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it('persists and deduplicates recent runs', () => {
    expect(loadRecentRuns()).toEqual([]);

    saveRecentRun({
      id: 'cmd-1',
      workflowName: 'direct',
      prompt: 'hello',
      actorId: 'Workflow:1',
      commandId: 'cmd-1',
      runId: 'run-1',
      status: 'completed',
      lastMessagePreview: 'done',
    });

    saveRecentRun({
      id: 'cmd-2',
      workflowName: 'direct',
      prompt: 'world',
      actorId: 'Workflow:2',
      commandId: 'cmd-2',
      runId: 'run-2',
      status: 'running',
      lastMessagePreview: 'streaming',
    });

    const deduplicated = saveRecentRun({
      id: 'cmd-1',
      workflowName: 'direct',
      prompt: 'updated',
      actorId: 'Workflow:1',
      commandId: 'cmd-1',
      runId: 'run-1',
      status: 'completed',
      lastMessagePreview: 'updated',
    });

    expect(deduplicated).toHaveLength(2);
    expect(deduplicated[0].id).toBe('cmd-1');
    expect(deduplicated[0].prompt).toBe('updated');
    expect(clearRecentRuns()).toEqual([]);
    expect(loadRecentRuns()).toEqual([]);
  });
});
