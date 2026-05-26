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
  return normalizedPathname === "/teams/new";
}

function readTeamScopeId(pathname: string): string {
  const match = pathname.match(/^\/teams\/([^/?#]+)/);
  if (!match) {
    return "";
  }

  const rawScopeId = match[1] ?? "";
  try {
    return decodeURIComponent(rawScopeId).trim();
  } catch {
    return rawScopeId.trim();
  }
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
    scopeId: readTeamScopeId(pathname),
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
    ? `/teams/${encodeURIComponent(normalizedScopeId)}`
    : "/teams";

  return buildScopeHref(path, { scopeId: normalizedScopeId }, extras);
}

export function resolveScopeOverviewPath(
  draft: ScopeQueryDraft,
  pathname = typeof window === "undefined" ? "" : window.location.pathname,
): string {
  if (pathname.startsWith("/teams/")) {
    const normalizedScopeId = trimOptional(draft.scopeId) || readTeamScopeId(pathname);
    if (normalizedScopeId) {
      return `/teams/${encodeURIComponent(normalizedScopeId)}`;
    }
  }

  return "/scopes/overview";
}

export function buildScopeOverviewHref(
  draft: ScopeQueryDraft,
  extras?: Record<string, string | null | undefined>,
  pathname = typeof window === "undefined" ? "" : window.location.pathname,
): string {
  return buildScopeHref(resolveScopeOverviewPath(draft, pathname), draft, extras);
}
