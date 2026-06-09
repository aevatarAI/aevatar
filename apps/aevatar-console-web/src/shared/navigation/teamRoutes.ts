import { buildStudioRoute } from '@/shared/studio/navigation';

type TeamDetailTab =
  | 'overview'
  | 'members';

type TeamToStudioMode = 'create-member' | 'edit-member' | 'build-member';

type QueryValue = string | undefined;
type TeamDetailRouteState = {
  readonly memberId: string;
  readonly runId: string;
  readonly scopeId: string;
  readonly serviceId: string;
  readonly tab: TeamDetailTab;
  readonly teamId: string;
  readonly testTeam: boolean;
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
}): string {
  return buildHref('/teams/new', {
    teamName: options?.teamName,
  });
}

export function buildTeamDetailHref(options: {
  memberId?: string;
  scopeId: string;
  teamId?: string;
  tab?: TeamDetailTab;
  serviceId?: string;
  runId?: string;
  testTeam?: boolean;
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
      testTeam: options.testTeam ? '1' : undefined,
    },
  );
}

export function buildTeamStudioHref(options: {
  memberId?: string;
  mode: TeamToStudioMode;
  returnTo?: string;
  scopeId: string;
  teamId: string;
}): string {
  const scopeId = trimOptional(options.scopeId);
  const teamId = trimOptional(options.teamId);
  if (!scopeId || !teamId) {
    return buildTeamsHref();
  }

  const memberId = trimOptional(options.memberId);
  const returnTo =
    trimOptional(options.returnTo) ||
    buildTeamDetailHref({
      memberId: memberId || undefined,
      scopeId,
      tab: 'members',
      teamId,
    });

  if (options.mode === 'create-member') {
    return buildStudioRoute({
      scopeId,
      teamId,
      tab: 'studio',
      intent: 'create-member',
      returnTo,
    });
  }

  if (!memberId) {
    return buildTeamDetailHref({
      scopeId,
      tab: 'members',
      teamId,
    });
  }

  return buildStudioRoute({
    scopeId,
    teamId,
    memberId,
    step: 'build',
    tab: options.mode === 'edit-member' ? 'studio' : undefined,
    returnTo,
  });
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
    testTeam: ['1', 'true', 'yes'].includes(trimOptional(params.get('testTeam')).toLowerCase()),
    workflowId: trimOptional(params.get('workflowId')),
  };
}

export type { TeamDetailRouteState, TeamDetailTab };
