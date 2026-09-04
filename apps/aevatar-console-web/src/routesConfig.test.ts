import { CONSOLE_HOME_ROUTE } from './shared/navigation/consoleHome';

describe('console routes', () => {
  const benchmarkEnvironmentKey = 'AEVATAR_WORKFLOW_CANVAS_BENCHMARK';

  function loadRoutes(): typeof import('../config/routes').default {
    let loadedRoutes!: typeof import('../config/routes').default;
    jest.isolateModules(() => {
      loadedRoutes = require('../config/routes')
        .default as typeof import('../config/routes').default;
    });
    return loadedRoutes;
  }

  function findRoute(
    routes: ReturnType<typeof loadRoutes>,
    path: string,
  ): Record<string, unknown> {
    const matchedRoute = routes.find((route) => route.path === path);
    if (!matchedRoute) {
      throw new Error(`Expected route ${path} to exist.`);
    }

    return matchedRoute as Record<string, unknown>;
  }

  function hasRoute(
    routes: ReturnType<typeof loadRoutes>,
    path: string,
  ): boolean {
    return routes.some((route) => route.path === path);
  }

  function findRouteIndex(
    routes: ReturnType<typeof loadRoutes>,
    path: string,
  ): number {
    const matchedIndex = routes.findIndex((route) => route.path === path);
    if (matchedIndex < 0) {
      throw new Error(`Expected route ${path} to exist.`);
    }

    return matchedIndex;
  }

  beforeEach(() => {
    jest.resetModules();
    delete process.env[benchmarkEnvironmentKey];
  });

  afterEach(() => {
    delete process.env[benchmarkEnvironmentKey];
  });

  it('registers the workflow canvas benchmark only for the exact opt-in value', () => {
    expect(hasRoute(loadRoutes(), '/workflow-canvas-benchmark')).toBe(false);

    process.env[benchmarkEnvironmentKey] = 'true';
    jest.resetModules();
    expect(hasRoute(loadRoutes(), '/workflow-canvas-benchmark')).toBe(false);

    process.env[benchmarkEnvironmentKey] = '1';
    jest.resetModules();
    const benchmarkRoute = findRoute(
      loadRoutes(),
      '/workflow-canvas-benchmark',
    );
    expect(benchmarkRoute).toEqual(
      expect.objectContaining({
        component: './workflow-canvas-benchmark',
        hideInMenu: true,
        layout: false,
      }),
    );
  });

  it('routes console home to Workflow Activity while preserving scoped Teams', () => {
    const routes = loadRoutes();

    expect(findRoute(routes, '/chat').hideInMenu).toBe(false);
    expect(findRoute(routes, '/chat').name).toBe('Chat');
    expect(findRoute(routes, '/chat').menuGroupKey).toBe('chat');
    expect(findRoute(routes, '/chat').icon).toBeUndefined();
    expect(findRoute(routes, '/scopes').hideInMenu).toBe(true);
    expect(findRoute(routes, '/studio').hideInMenu).toBe(true);
    expect(findRoute(routes, '/runtime/runs').hideInMenu).toBeUndefined();
    expect(findRoute(routes, '/runtime/runs').menuGroupKey).toBe('platform');
    expect(findRoute(routes, '/scopes/overview').hideInMenu).toBe(true);
    expect(findRoute(routes, '/scopes').name).toBeUndefined();
    expect(findRoute(routes, '/scopes').component).toBeUndefined();
    for (const path of ['/', '/overview', '/scopes']) {
      expect(findRoute(routes, path).redirect).toBe(CONSOLE_HOME_ROUTE);
    }
    expect(findRouteIndex(routes, '/chat')).toBeLessThan(
      findRouteIndex(routes, '/scopes'),
    );
    expect(hasRoute(routes, '/teams')).toBe(false);
    expect(hasRoute(routes, '/teams/new')).toBe(false);
    expect(hasRoute(routes, '/teams/:scopeId')).toBe(false);
    expect(hasRoute(routes, '/teams/:scopeId/:teamId')).toBe(false);
    expect(
      hasRoute(routes, '/teams/:scopeId/:teamId/members/new/workflow'),
    ).toBe(false);
    expect(
      hasRoute(routes, '/teams/:scopeId/:teamId/members/:memberId/workflow'),
    ).toBe(false);
    expect(
      hasRoute(routes, '/teams/:scopeId/:teamId/members/:memberId/invoke'),
    ).toBe(false);
    expect(
      hasRoute(routes, '/teams/:scopeId/:teamId/members/:memberId/runs'),
    ).toBe(false);
    expect(findRoute(routes, '/scopes/:scopeId/teams').component).toBe(
      './teams',
    );
    expect(findRoute(routes, '/scopes/:scopeId/teams').parentKeys).toEqual([
      '/scopes',
    ]);
    expect(findRoute(routes, '/scopes/:scopeId/teams/new').component).toBe(
      './teams/new',
    );
    expect(findRoute(routes, '/scopes/:scopeId/teams/new').parentKeys).toEqual([
      '/scopes',
    ]);
    expect(findRoute(routes, '/scopes/:scopeId/teams/:teamId').component).toBe(
      './teams/detail',
    );
    expect(
      findRoute(routes, '/scopes/:scopeId/teams/:teamId').parentKeys,
    ).toEqual(['/scopes']);
    expect(
      findRoute(routes, '/scopes/:scopeId/teams/:teamId/members/new/workflow')
        .component,
    ).toBe('./team-member-workflow-studio');
    expect(
      findRoute(
        routes,
        '/scopes/:scopeId/teams/:teamId/members/:memberId/workflow',
      ).component,
    ).toBe('./team-member-workflow-studio');
    expect(
      findRoute(
        routes,
        '/scopes/:scopeId/teams/:teamId/members/:memberId/invoke',
      ).component,
    ).toBe('./team-member-invoke');
    expect(
      findRoute(routes, '/scopes/:scopeId/teams/:teamId/members/:memberId/runs')
        .component,
    ).toBe('./runtime-published-runs');
    expect(
      findRoute(
        routes,
        '/scopes/:scopeId/teams/:teamId/members/:memberId/automations',
      ).component,
    ).toBe('./teams/detail');
    expect(
      findRouteIndex(
        routes,
        '/scopes/:scopeId/teams/:teamId/members/new/workflow',
      ),
    ).toBeLessThan(findRouteIndex(routes, '/scopes/:scopeId/teams/:teamId'));
    expect(
      findRouteIndex(
        routes,
        '/scopes/:scopeId/teams/:teamId/members/:memberId/workflow',
      ),
    ).toBeLessThan(findRouteIndex(routes, '/scopes/:scopeId/teams/:teamId'));
    expect(
      findRouteIndex(
        routes,
        '/scopes/:scopeId/teams/:teamId/members/:memberId/runs',
      ),
    ).toBeLessThan(findRouteIndex(routes, '/scopes/:scopeId/teams/:teamId'));
    expect(
      findRouteIndex(
        routes,
        '/scopes/:scopeId/teams/:teamId/members/:memberId/automations',
      ),
    ).toBeLessThan(findRouteIndex(routes, '/scopes/:scopeId/teams/:teamId'));
    expect(findRoute(routes, '/runtime/gagents').name).toBe('Members');
    expect(findRoute(routes, '/scopes/assets').name).toBeUndefined();
    expect(findRoute(routes, '/scopes/invoke').name).toBeUndefined();
    expect(findRoute(routes, '/scopes/overview').component).toBe(
      './scopes/overview',
    );
    expect(hasRoute(routes, '/workflows')).toBe(true);
    expect(findRoute(routes, '/workflows').redirect).toBe('/runtime/workflows');
    expect(hasRoute(routes, '/primitives')).toBe(true);
    expect(findRoute(routes, '/primitives').redirect).toBe(
      '/runtime/primitives',
    );
    expect(hasRoute(routes, '/runs')).toBe(true);
    expect(findRoute(routes, '/runs').redirect).toBe('/runtime/runs');
    expect(hasRoute(routes, '/actors')).toBe(true);
    expect(findRoute(routes, '/actors').redirect).toBe('/runtime/explorer');
    expect(hasRoute(routes, '/gagents')).toBe(true);
    expect(findRoute(routes, '/gagents').redirect).toBe('/runtime/gagents');
    expect(hasRoute(routes, '/mission-control')).toBe(true);
    expect(findRoute(routes, '/mission-control').redirect).toBe(
      '/runtime/mission-control',
    );
    expect(findRoute(routes, '/runtime/mission-control').hideInMenu).toBe(true);
    expect(hasRoute(routes, '/mission-wall')).toBe(true);
    expect(findRoute(routes, '/mission-wall').redirect).toBe(
      '/runtime/mission-wall',
    );
    expect(findRoute(routes, '/runtime/mission-wall').hideInMenu).toBe(true);
    expect(findRoute(routes, '/runtime/mission-wall').name).toBeUndefined();
    expect(findRoute(routes, '/runtime/explorer').menuGroupKey).toBe(
      'platform',
    );
    expect(findRoute(routes, '/runtime/explorer/detail').hideInMenu).toBe(true);
    expect(findRoute(routes, '/runtime/explorer/detail').parentKeys).toEqual([
      '/runtime/explorer',
    ]);
    expect(findRoute(routes, '/services').menuGroupKey).toBe('platform');
    expect(findRoute(routes, '/deployments').menuGroupKey).toBe('platform');
    expect(findRoute(routes, '/governance').menuGroupKey).toBe('platform');
    expect(findRoute(routes, '/governance/audit').redirect).toBe(
      '/governance?view=changes',
    );
    expect(findRouteIndex(routes, '/services')).toBeLessThan(
      findRouteIndex(routes, '/governance'),
    );
    expect(findRouteIndex(routes, '/governance')).toBeLessThan(
      findRouteIndex(routes, '/deployments'),
    );
    expect(findRouteIndex(routes, '/deployments')).toBeLessThan(
      findRouteIndex(routes, '/runtime/explorer'),
    );
    expect(findRoute(routes, '/settings').name).toBe('Settings');
  });

  it('isolates Workflow Activity vNext under its hidden scoped namespace', () => {
    const routes = loadRoutes();
    const namespace = '/scopes/:scopeId/workflow-activity-vnext';
    const expectedRoutes = [
      namespace,
      `${namespace}/workflows`,
      `${namespace}/workflows/new`,
      `${namespace}/workflows/new/templates`,
      `${namespace}/workflows/:workflowId`,
      `${namespace}/activity`,
      `${namespace}/activity/:runId`,
      `${namespace}/settings`,
    ];

    for (const path of expectedRoutes) {
      expect(findRoute(routes, path).hideInMenu).toBe(true);
    }

    expect(findRoute(routes, namespace).redirect).toBe(
      `${namespace}/workflows`,
    );
    expect(findRoute(routes, `${namespace}/workflows`).component).toBe(
      './workflow-activity-vnext',
    );
    expect(
      findRoute(routes, `${namespace}/workflows/new/templates`).component,
    ).toBe('./workflow-activity-vnext');
    expect(findRouteIndex(routes, `${namespace}/workflows/new`)).toBeLessThan(
      findRouteIndex(routes, `${namespace}/workflows/:workflowId`),
    );
    expect(
      findRouteIndex(routes, `${namespace}/workflows/new/templates`),
    ).toBeLessThan(
      findRouteIndex(routes, `${namespace}/workflows/:workflowId`),
    );

    expect(findRoute(routes, '/workflows').redirect).toBe('/runtime/workflows');
    expect(findRoute(routes, '/runs').redirect).toBe('/runtime/runs');
    expect(findRoute(routes, '/').redirect).toBe(CONSOLE_HOME_ROUTE);
  });
});
