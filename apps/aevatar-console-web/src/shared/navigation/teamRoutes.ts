type TeamDetailTab =
  | 'overview'
  | 'topology'
  | 'events'
  | 'members'
  | 'connectors'
  | 'advanced';

type QueryValue = string | undefined;

function buildHref(
  pathname: string,
  query?: Record<string, QueryValue>,
): string {
  if (!query) {
    return pathname;
  }

  const params = new URLSearchParams();
  Object.entries(query).forEach(([key, value]) => {
    const normalized = value?.trim();
    if (normalized) {
      params.set(key, normalized);
    }
  });

  const search = params.toString();
  return search ? `${pathname}?${search}` : pathname;
}

export function buildTeamsHref(): string {
  return '/teams';
}

export function buildTeamCreateHref(): string {
  return '/teams/new';
}

export function buildTeamDetailHref(options: {
  scopeId: string;
  tab?: TeamDetailTab;
  serviceId?: string;
  runId?: string;
}): string {
  const scopeId = options.scopeId.trim();
  if (!scopeId) {
    return buildTeamsHref();
  }

  return buildHref(`/teams/${encodeURIComponent(scopeId)}`, {
    tab: options.tab,
    serviceId: options.serviceId,
    runId: options.runId,
  });
}

export type { TeamDetailTab };
