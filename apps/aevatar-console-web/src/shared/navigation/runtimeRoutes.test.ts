import {
  buildRuntimeExplorerHref,
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
    expect(
      buildRuntimeRunsHref({
        actorId: 'actor://selected',
        returnTo: buildRuntimeExplorerHref({
          actorId: 'actor://selected',
          runId: 'run-1',
        }),
      }),
    ).toContain(
      'returnTo=%2Fruntime%2Fexplorer%2Fdetail%3FactorId%3Dactor%253A%252F%252Fselected%26runId%3Drun-1',
    );
  });
});
