import {
  buildRuntimeExplorerHref,
  buildRuntimeMissionControlHref,
  buildRuntimeMissionWallHref,
  buildRuntimeWorkflowRunHref,
  buildRuntimeRunsHref,
} from './runtimeRoutes';

describe('runtimeRoutes', () => {
  it('keeps the topology list path when no actor is selected', () => {
    expect(buildRuntimeExplorerHref()).toBe('/runtime/explorer');
  });

  it('routes actor-specific topology links to the dedicated detail page', () => {
    expect(
      buildRuntimeExplorerHref({
        actorId: 'actor://selected',
        runId: 'run-1',
        scopeId: 'scope-a',
        serviceId: 'draft',
      }),
    ).toBe(
      '/runtime/explorer/detail?actorId=actor%3A%2F%2Fselected&runId=run-1&scopeId=scope-a&serviceId=draft',
    );
  });

  it('lets runs return back to topology detail routes', () => {
    const href = buildRuntimeRunsHref({
      actorId: 'actor://selected',
      runId: 'run-1',
      returnTo: buildRuntimeExplorerHref({
        actorId: 'actor://selected',
        runId: 'run-1',
      }),
    });
    const url = new URL(href, 'https://console.aevatar.test');

    expect(url.pathname).toBe('/runtime/runs');
    expect(url.searchParams.get('actorId')).toBe('actor://selected');
    expect(url.searchParams.get('runId')).toBe('run-1');
    expect(url.searchParams.get('returnTo')).toBe(
      '/runtime/explorer/detail?actorId=actor%3A%2F%2Fselected&runId=run-1',
    );
  });

  it('preserves workflow catalog context for workflow run handoffs', () => {
    const href = buildRuntimeWorkflowRunHref('demo_flow');
    const url = new URL(href, 'https://console.aevatar.test');

    expect(url.pathname).toBe('/runtime/runs');
    expect(url.searchParams.get('route')).toBe('demo_flow');
    expect(url.searchParams.get('returnTo')).toBe(
      '/runtime/workflows?workflow=demo_flow',
    );
  });

  it('builds Mission Control deep links with live run context', () => {
    expect(
      buildRuntimeMissionControlHref({
        actorId: 'actor://selected',
        autoStream: false,
        endpointId: 'chat',
        prompt: 'inspect this run',
        runId: 'run-1',
        scopeId: 'scope-a',
        serviceId: 'draft',
      }),
    ).toBe(
      '/runtime/mission-control?actorId=actor%3A%2F%2Fselected&autoStream=false&endpointId=chat&prompt=inspect+this+run&runId=run-1&scopeId=scope-a&serviceId=draft',
    );
  });

  it('omits empty Mission Control query values', () => {
    expect(
      buildRuntimeMissionControlHref({
        runId: 'run-1',
        scopeId: 'scope-a',
      }),
    ).toBe('/runtime/mission-control?runId=run-1&scopeId=scope-a');
  });

  it('builds Mission Wall links with wall-level focus context', () => {
    expect(
      buildRuntimeMissionWallHref({
        focusRunId: 'run-1',
        scopeId: 'scope-a',
        teamId: 'team-a',
      }),
    ).toBe('/runtime/mission-wall?focusRunId=run-1&scopeId=scope-a&teamId=team-a');
  });
});
