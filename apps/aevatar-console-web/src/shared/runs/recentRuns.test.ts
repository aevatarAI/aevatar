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
      routeName: 'direct',
      prompt: 'hello',
      actorId: 'Workflow:1',
      commandId: 'cmd-1',
      runId: 'run-1',
      status: 'completed',
      lastMessagePreview: 'done',
    });

    saveRecentRun({
      id: 'cmd-2',
      routeName: 'direct',
      prompt: 'world',
      actorId: 'Workflow:2',
      commandId: 'cmd-2',
      runId: 'run-2',
      status: 'running',
      lastMessagePreview: 'streaming',
    });

    const deduplicated = saveRecentRun({
      id: 'cmd-1',
      routeName: 'direct',
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

  it('keeps an empty route name so chat runs can fall back to the default label', () => {
    saveRecentRun({
      id: 'cmd-chat',
      endpointId: 'chat',
      prompt: 'hello',
      status: 'running',
    });

    expect(loadRecentRuns()[0]?.routeName).toBe('');
  });

  it('keeps reading legacy workflowName and serviceId fields', () => {
    window.localStorage.setItem(
      'aevatar-console-recent-runs',
      JSON.stringify([
        {
          id: 'cmd-legacy',
          workflowName: 'direct',
          serviceId: 'svc-1',
          endpointId: 'chat',
          status: 'running',
        },
      ])
    );

    expect(loadRecentRuns()).toEqual([
      expect.objectContaining({
        id: 'cmd-legacy',
        routeName: 'direct',
        serviceOverrideId: 'svc-1',
      }),
    ]);
  });

  it('persists observed events for recent-run replay', () => {
    saveRecentRun({
      id: 'cmd-observed',
      endpointId: 'chat',
      routeName: 'direct',
      scopeId: 'scope-1',
      status: 'finished',
      observedEvents: [
        {
          type: 'RUN_STARTED',
          runId: 'run-1',
          threadId: 'thread-1',
          timestamp: Date.now(),
        } as any,
      ],
    });

    expect(loadRecentRuns()).toEqual([
      expect.objectContaining({
        id: 'cmd-observed',
        observedEvents: [
          expect.objectContaining({
            type: 'RUN_STARTED',
            runId: 'run-1',
          }),
        ],
      }),
    ]);
  });
});
