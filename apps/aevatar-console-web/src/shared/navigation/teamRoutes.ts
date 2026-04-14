type TeamDetailTab =
  | 'overview'
  | 'topology'
  | 'events'
  | 'members'
  | 'connectors'
  | 'advanced';

type QueryValue = string | undefined;
type TeamDetailRouteState = {
  readonly runId: string;
  readonly scopeId: string;
  readonly serviceId: string;
  readonly tab: TeamDetailTab;
  readonly workflowId: string;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function decodePathSegment(value: string): string {
  try {
    return decodeURIComponent(value).trim();
  } catch {
    return value.trim();
  }
}

function parseTeamTab(
  value: string | null | undefined,
  fallback: TeamDetailTab = 'overview',
): TeamDetailTab {
  switch (trimOptional(value).toLowerCase()) {
    case 'overview':
    case 'topology':
    case 'events':
    case 'members':
    case 'connectors':
    case 'advanced':
      return trimOptional(value).toLowerCase() as TeamDetailTab;
    default:
      return fallback;
  }
}

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
  workflowId?: string;
}): string {
  const scopeId = trimOptional(options.scopeId);
  if (!scopeId) {
    return buildTeamsHref();
  }

  return buildHref(`/teams/${encodeURIComponent(scopeId)}`, {
    workflowId: options.workflowId,
    tab: options.tab,
    serviceId: options.serviceId,
    runId: options.runId,
  });
}

export function readTeamDetailRouteState(
  search = typeof window === 'undefined' ? '' : window.location.search,
  pathname = typeof window === 'undefined' ? '' : window.location.pathname,
): TeamDetailRouteState {
  const params = new URLSearchParams(search);
  const pathnameSegments = pathname.split('/').filter(Boolean);
  const scopeIdFromPath =
    pathnameSegments[0] === 'teams' && pathnameSegments[1]
      ? decodePathSegment(pathnameSegments[1])
      : '';
  const defaultTab: TeamDetailTab = scopeIdFromPath ? 'topology' : 'overview';

  return {
    runId: trimOptional(params.get('runId')),
    scopeId: trimOptional(params.get('scopeId')) || scopeIdFromPath,
    serviceId: trimOptional(params.get('serviceId')),
    tab: parseTeamTab(params.get('tab'), defaultTab),
    workflowId: trimOptional(params.get('workflowId')),
  };
}

export type { TeamDetailRouteState, TeamDetailTab };
