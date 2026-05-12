type TeamDetailTab =
  | 'overview'
  | 'topology'
  | 'events'
  | 'members';

type QueryValue = string | undefined;
type TeamDetailRouteState = {
  readonly memberId: string;
  readonly runId: string;
  readonly scopeId: string;
  readonly serviceId: string;
  readonly tab: TeamDetailTab;
  readonly teamId: string;
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

export function buildTeamCreateHref(options?: {
  teamName?: string;
  entryName?: string;
  teamDraftWorkflowId?: string;
  teamDraftWorkflowName?: string;
}): string {
  return buildHref('/teams/new', {
    teamName: options?.teamName,
    entryName: options?.entryName,
    teamDraftWorkflowId: options?.teamDraftWorkflowId,
    teamDraftWorkflowName: options?.teamDraftWorkflowName,
  });
}

export function buildTeamDetailHref(options: {
  memberId?: string;
  scopeId: string;
  teamId?: string;
  tab?: TeamDetailTab;
  serviceId?: string;
  runId?: string;
  workflowId?: string;
}): string {
  const scopeId = trimOptional(options.scopeId);
  if (!scopeId) {
    return buildTeamsHref();
  }

  const teamId = trimOptional(options.teamId);
  if (!teamId) {
    return buildHref(buildTeamsHref(), {
      scopeId,
    });
  }

  return buildHref(
    `/teams/${encodeURIComponent(scopeId)}/${encodeURIComponent(teamId)}`,
    {
      memberId: options.memberId,
      workflowId: options.workflowId,
      tab: options.tab,
      serviceId: options.serviceId,
      runId: options.runId,
    },
  );
}

export function readTeamDetailRouteState(
  search = typeof window === 'undefined' ? '' : window.location.search,
  pathname = typeof window === 'undefined' ? '' : window.location.pathname,
): TeamDetailRouteState {
  const params = new URLSearchParams(search);
  const pathnameSegments = pathname.split('/').filter(Boolean);
  const isTeamPath = pathnameSegments[0] === 'teams';
  const scopeIdFromPath =
    isTeamPath && pathnameSegments[1]
      ? decodePathSegment(pathnameSegments[1])
      : '';
  const teamIdFromPath =
    isTeamPath && pathnameSegments[2]
      ? decodePathSegment(pathnameSegments[2])
      : '';
  const defaultTab: TeamDetailTab = 'overview';

  return {
    memberId: trimOptional(params.get('memberId')),
    runId: trimOptional(params.get('runId')),
    scopeId: scopeIdFromPath || trimOptional(params.get('scopeId')),
    serviceId: trimOptional(params.get('serviceId')),
    tab: parseTeamTab(params.get('tab'), defaultTab),
    teamId: teamIdFromPath || trimOptional(params.get('teamId')),
    workflowId: trimOptional(params.get('workflowId')),
  };
}

export type { TeamDetailRouteState, TeamDetailTab };
