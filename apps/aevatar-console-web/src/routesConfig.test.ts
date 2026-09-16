import { CONSOLE_HOME_ROUTE } from './shared/navigation/consoleHome';

function loadRoutes(): typeof import('../config/routes').default {
  let routes!: typeof import('../config/routes').default;
  jest.isolateModules(() => {
    routes = require('../config/routes').default;
  });
  return routes;
}

afterEach(() => {
  delete process.env.AEVATAR_WORKFLOW_CANVAS_BENCHMARK;
});

it('exposes only canonical resource pages, authentication and home entries in production', () => {
  delete process.env.AEVATAR_WORKFLOW_CANVAS_BENCHMARK;
  const routes = loadRoutes();
  expect(routes.map((route) => route.path)).toEqual([
    '/login',
    '/auth/callback',
    '/',
    '/overview',
    '/scopes',
    '/workflows',
    '/scopes/:scopeId',
    '/scopes/:scopeId/workflows',
    '/scopes/:scopeId/workflows/new',
    '/scopes/:scopeId/workflows/new/templates',
    '/scopes/:scopeId/workflows/:workflowId',
    '/scopes/:scopeId/activity',
    '/scopes/:scopeId/activity/:runId',
    '/scopes/:scopeId/channels',
    '/scopes/:scopeId/channels/connect/telegram',
    '/scopes/:scopeId/channels/:registrationId/edit',
    '/scopes/:scopeId/channels/:registrationId',
    '/scopes/:scopeId/settings',
    '/*',
  ]);
  for (const path of ['/', '/overview', '/scopes']) {
    expect(routes.find((route) => route.path === path)).toMatchObject({
      redirect: CONSOLE_HOME_ROUTE,
    });
  }
  expect(routes.find((route) => route.path === '/workflows')).toMatchObject({
    component: './workflow-activity-vnext/WorkflowHomePage',
  });
  expect(
    routes.find((route) => route.path === '/scopes/:scopeId'),
  ).toMatchObject({ redirect: '/scopes/:scopeId/workflows' });
  for (const route of routes.filter((route) =>
    route.path.startsWith('/scopes/:scopeId/'),
  )) {
    expect(route).toMatchObject({
      component: './workflow-activity-vnext',
      hideInMenu: true,
    });
  }
});

it('keeps the canvas benchmark behind the exact development opt-in', () => {
  process.env.AEVATAR_WORKFLOW_CANVAS_BENCHMARK = 'true';
  expect(
    loadRoutes().some((route) => route.path === '/workflow-canvas-benchmark'),
  ).toBe(false);
  process.env.AEVATAR_WORKFLOW_CANVAS_BENCHMARK = '1';
  expect(
    loadRoutes().find((route) => route.path === '/workflow-canvas-benchmark'),
  ).toMatchObject({ component: './workflow-canvas-benchmark', layout: false });
});
