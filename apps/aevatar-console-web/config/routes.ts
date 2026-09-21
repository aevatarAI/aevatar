import { CONSOLE_HOME_ROUTE } from '../src/shared/navigation/consoleHome';

const workflowCanvasBenchmarkRoutes =
  process.env.AEVATAR_WORKFLOW_CANVAS_BENCHMARK === '1'
    ? [
        {
          path: '/workflow-canvas-benchmark',
          component: './workflow-canvas-benchmark',
          hideInMenu: true,
          layout: false,
        },
      ]
    : [];

export default [
  ...workflowCanvasBenchmarkRoutes,
  { path: '/login', component: './login', layout: false },
  { path: '/auth/callback', component: './auth/callback', layout: false },
  ...['/', '/overview', '/scopes'].map((path) => ({
    path,
    redirect: CONSOLE_HOME_ROUTE,
    hideInMenu: true,
  })),
  {
    path: '/workflows',
    component: './workflow-activity-vnext/WorkflowHomePage',
    hideInMenu: true,
  },
  ...[
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
  ].map((resourcePath) => ({
    path: `/scopes/:scopeId/${resourcePath}`,
    component: './workflow-activity-vnext',
    hideInMenu: true,
  })),
  { path: '/*', component: '404', layout: false },
];
