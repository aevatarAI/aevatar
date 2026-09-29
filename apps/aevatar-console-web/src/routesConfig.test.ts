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

  it('opens the current workflow console from every home entry', () => {
    const routes = loadRoutes();
    for (const path of ['/', '/overview', '/scopes']) {
      expect(findRoute(routes, path).redirect).toBe(CONSOLE_HOME_ROUTE);
    }
    expect(findRoute(routes, '/workflows')).toMatchObject({
      component: './workflow-activity-vnext/WorkflowHomePage',
      hideInMenu: true,
    });
  });

  it('registers only the current console, auth, home and not-found surfaces', () => {
    const routes = loadRoutes();
    const resources = [
      'workflows',
      'workflows/new',
      'workflows/new/templates',
      'workflows/:workflowId',
      'activity',
      'activity/:runId',
      'channels',
      'channels/bind/:botId',
      'channels/:registrationId/edit',
      'channels/:registrationId',
      'settings',
    ];
    expect(routes.map((route) => route.path).sort()).toEqual(
      [
        '/',
        '/overview',
        '/scopes',
        '/workflows',
        '/login',
        '/auth/callback',
        '/*',
        ...resources.map((resource) => `/scopes/:scopeId/${resource}`),
      ].sort(),
    );
    for (const resource of resources) {
      const route = findRoute(routes, `/scopes/:scopeId/${resource}`);
      expect(route).toMatchObject({
        component: './workflow-activity-vnext',
        hideInMenu: true,
      });
      // Keep the shared authenticated providers around the local shell.
      expect(route.layout).not.toBe(false);
    }
    expect(findRoute(routes, '/*')).toMatchObject({
      component: '404',
      layout: false,
    });
    expect(
      findRouteIndex(routes, '/scopes/:scopeId/workflows/new'),
    ).toBeLessThan(
      findRouteIndex(routes, '/scopes/:scopeId/workflows/:workflowId'),
    );
    expect(
      findRouteIndex(routes, '/scopes/:scopeId/channels/bind/:botId'),
    ).toBeLessThan(
      findRouteIndex(routes, '/scopes/:scopeId/channels/:registrationId'),
    );
  });

  it('keeps the preserved legacy page routes out of the public router', () => {
    const routes = loadRoutes();
    const { legacyConsoleRoutes } =
      require('../config/legacyRoutes') as typeof import('../config/legacyRoutes');
    expect(legacyConsoleRoutes).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          path: '/scopes/:scopeId/teams',
          component: './teams',
        }),
        expect.objectContaining({
          path: '/runtime/gagents',
          component: './gagents',
        }),
        expect.objectContaining({
          path: '/scopes/:scopeId/teams/:teamId/members/:memberId/workflow',
          component: './team-member-workflow-studio',
        }),
      ]),
    );
    for (const route of legacyConsoleRoutes) {
      if (route.path === '/scopes') continue; // Account home alias, no legacy page.
      expect(hasRoute(routes, route.path)).toBe(false);
    }
    expect(
      routes.some((route) => route.path.includes('workflow-activity-vnext')),
    ).toBe(false);
  });
});
