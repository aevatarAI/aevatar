import React from 'react';
import routes from '../config/routes';
import { useConsoleLocation } from '../src/pages/workflow-activity-vnext/hooks/useConsoleLocation';

// Existing feature tests exercise the same independent entries as Umi. Keep
// this loader test-only; a production page must never import all route owners.
export default function WorkflowActivityRouteTestHarness() {
  const { pathname } = useConsoleLocation();
  const matchedRoute = routes.find((route) => {
    if (!route.path.startsWith('/scopes/:scopeId/')) return false;
    const pattern = route.path
      .split('/')
      .map((segment) =>
        segment.startsWith(':')
          ? '[^/]+'
          : segment.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'),
      )
      .join('/');
    return new RegExp(`^${pattern}$`).test(pathname);
  });
  if (!matchedRoute || !('component' in matchedRoute)) {
    throw new Error(`No console route registered for ${pathname}`);
  }
  const { default: Route } = require(
    `../src/pages/${matchedRoute.component.slice(2)}`,
  ) as { default: React.ComponentType };
  return <Route />;
}
