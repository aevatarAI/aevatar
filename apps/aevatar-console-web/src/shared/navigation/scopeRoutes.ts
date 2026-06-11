export type ScopeQueryDraft = {
  scopeId: string;
};

function readString(value: string | null): string {
  return value?.trim() ?? "";
}

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function isTeamCreatePath(pathname: string): boolean {
  const normalizedPathname = pathname.split(/[?#]/)[0]?.replace(/\/+$/, "") ?? "";
  return /^\/scopes\/[^/]+\/teams\/new$/.test(normalizedPathname);
}

function readPathScopeId(pathname: string): string {
  const scopedMatch = pathname.match(/^\/scopes\/([^/?#]+)\/teams(?:\/|$)/);
  if (!scopedMatch) {
    return "";
  }

  const rawScopeId = scopedMatch[1] ?? "";

  try {
    return decodeURIComponent(rawScopeId).trim();
  } catch {
    return rawScopeId.trim();
  }
}

function isTeamWorkspacePath(pathname: string): boolean {
  return /^\/scopes\/[^/?#]+\/teams(?:\/|$)/.test(pathname);
}

export function normalizeScopeDraft(draft: ScopeQueryDraft): ScopeQueryDraft {
  return {
    scopeId: trimOptional(draft.scopeId),
  };
}

export function readScopeQueryDraft(
  search = typeof window === "undefined" ? "" : window.location.search,
  pathname = typeof window === "undefined" ? "" : window.location.pathname,
): ScopeQueryDraft {
  const params = new URLSearchParams(search);
  const isCreatePath = isTeamCreatePath(pathname);
  const pathScopeId = readPathScopeId(pathname);
  if (pathScopeId) {
    return {
      scopeId: pathScopeId,
    };
  }

  const queryScopeId = readString(params.get("scopeId"));
  if (queryScopeId && !(isCreatePath && queryScopeId === "new")) {
    return {
      scopeId: queryScopeId,
    };
  }

  if (isCreatePath) {
    return {
      scopeId: "",
    };
  }

  return {
    scopeId: "",
  };
}

function buildScopeParams(
  draft: ScopeQueryDraft,
  extras?: Record<string, string | null | undefined>,
): URLSearchParams {
  const params = new URLSearchParams();

  if (draft.scopeId.trim()) {
    params.set("scopeId", draft.scopeId.trim());
  }

  for (const [key, value] of Object.entries(extras ?? {})) {
    const normalized = value?.trim();
    if (normalized) {
      params.set(key, normalized);
    }
  }

  return params;
}

export function buildScopeHref(
  path: string,
  draft: ScopeQueryDraft,
  extras?: Record<string, string | null | undefined>,
): string {
  const suffix = buildScopeParams(draft, extras).toString();
  return suffix ? `${path}?${suffix}` : path;
}

export function buildTeamWorkspaceRoute(
  scopeId: string,
  extras?: Record<string, string | null | undefined>,
): string {
  const normalizedScopeId = trimOptional(scopeId);
  const path = normalizedScopeId
    ? `/scopes/${encodeURIComponent(normalizedScopeId)}/teams`
    : "/scopes";

  return buildScopeHref(path, { scopeId: "" }, extras);
}

export function buildTeamCreateRoute(
  scopeId: string,
  extras?: Record<string, string | null | undefined>,
): string {
  const normalizedScopeId = trimOptional(scopeId);
  const path = normalizedScopeId
    ? `/scopes/${encodeURIComponent(normalizedScopeId)}/teams/new`
    : "/scopes";

  return buildScopeHref(path, { scopeId: "" }, extras);
}

export function resolveScopeOverviewPath(
  draft: ScopeQueryDraft,
  pathname = typeof window === "undefined" ? "" : window.location.pathname,
): string {
  if (isTeamWorkspacePath(pathname)) {
    const normalizedScopeId = trimOptional(draft.scopeId) || readPathScopeId(pathname);
    if (normalizedScopeId) {
      return `/scopes/${encodeURIComponent(normalizedScopeId)}/teams`;
    }
  }

  return "/scopes/overview";
}

export function buildScopeOverviewHref(
  draft: ScopeQueryDraft,
  extras?: Record<string, string | null | undefined>,
  pathname = typeof window === "undefined" ? "" : window.location.pathname,
): string {
  const overviewPath = resolveScopeOverviewPath(draft, pathname);
  const routeDraft = /^\/scopes\/[^/]+\/teams(?:\/|$)/.test(overviewPath)
    ? { scopeId: "" }
    : draft;

  return buildScopeHref(overviewPath, routeDraft, extras);
}
