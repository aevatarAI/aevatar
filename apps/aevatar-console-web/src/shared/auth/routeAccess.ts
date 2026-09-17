const benchmarkEnabled = process.env.AEVATAR_WORKFLOW_CANVAS_BENCHMARK === '1';
export const PUBLIC_ROUTES = new Set([
  '/login',
  '/auth/callback',
  ...(benchmarkEnabled ? ['/workflow-canvas-benchmark'] : []),
]);

export function requiresGlobalAuthGate(pathname: string): boolean {
  return !PUBLIC_ROUTES.has(pathname) && pathname !== '/studio';
}
