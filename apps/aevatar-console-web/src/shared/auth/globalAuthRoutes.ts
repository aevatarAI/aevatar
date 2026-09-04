export const PUBLIC_ROUTES = new Set(["/login", "/auth/callback"]);

const SELF_MANAGED_AUTH_ROUTES = new Set(["/studio"]);

export function requiresGlobalAuthGate(pathname: string): boolean {
  return !PUBLIC_ROUTES.has(pathname) && !SELF_MANAGED_AUTH_ROUTES.has(pathname);
}
