export const STUDIO_HOST_BODY_CLASS = "aevatar-studio-host";

const TEAM_MEMBER_WORKFLOW_STUDIO_ROUTE_PATTERN =
  /^\/scopes\/[^/]+\/teams\/[^/]+\/members\/(?:new|[^/]+)\/workflow$/;

const STUDIO_HOST_ROUTES = new Set([
  "/studio",
  "/scopes/:scopeId/teams/:teamId/members/new/workflow",
  "/scopes/:scopeId/teams/:teamId/members/:memberId/workflow",
]);

export function isStudioHostRoute(pathname: string): boolean {
  return (
    STUDIO_HOST_ROUTES.has(pathname) ||
    TEAM_MEMBER_WORKFLOW_STUDIO_ROUTE_PATTERN.test(pathname)
  );
}

export function syncStudioHostBodyClass(enabled: boolean): () => void {
  if (typeof document === "undefined") {
    return () => {};
  }

  document.body.classList.toggle(STUDIO_HOST_BODY_CLASS, enabled);

  return () => {
    if (!enabled) {
      return;
    }

    document.body.classList.remove(STUDIO_HOST_BODY_CLASS);
  };
}
