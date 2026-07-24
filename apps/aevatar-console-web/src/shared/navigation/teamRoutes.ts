import { buildStudioRoute } from '@/shared/studio/navigation';

type TeamDetailTab =
  | 'overview'
  | 'automations'
  | 'members';

type TeamToStudioMode = 'create-member' | 'edit-member' | 'build-member';

type QueryValue = string | undefined;
type TeamDetailRouteState = {
  readonly memberId: string;
  readonly routeMemberId: string;
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
    case 'automations':
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
  return '/scopes';
}

export function buildTeamCreateHref(options?: {
  scopeId?: string;
  teamName?: string;
}): string {
  const scopeId = trimOptional(options?.scopeId);
  const pathname = scopeId
    ? `/scopes/${encodeURIComponent(scopeId)}/teams/new`
    : buildTeamsHref();

  return buildHref(pathname, {
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
    return `/scopes/${encodeURIComponent(scopeId)}/teams`;
  }

  return buildHref(
    `/scopes/${encodeURIComponent(scopeId)}/teams/${encodeURIComponent(teamId)}`,
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

export function buildTeamMemberWorkflowStudioHref(options: {
  memberId?: string;
  mode: 'create-member' | 'edit-member';
  scopeId: string;
  teamId: string;
  workflowId?: string;
  workflowSource?: 'published';
}): string {
  const scopeId = trimOptional(options.scopeId);
  const teamId = trimOptional(options.teamId);
  if (!scopeId || !teamId) {
    return buildTeamsHref();
  }

  if (options.mode === 'create-member') {
    return `/scopes/${encodeURIComponent(scopeId)}/teams/${encodeURIComponent(teamId)}/members/new/workflow`;
  }

  const memberId = trimOptional(options.memberId);
  if (!memberId) {
    return buildTeamDetailHref({
      scopeId,
      tab: 'members',
      teamId,
    });
  }

  return buildHref(
    `/scopes/${encodeURIComponent(scopeId)}/teams/${encodeURIComponent(teamId)}/members/${encodeURIComponent(memberId)}/workflow`,
    {
      workflowId: options.workflowId,
      workflowSource: options.workflowSource,
    },
  );
}

export function buildTeamMemberInvokeHref(options: {
  memberId?: string;
  scopeId: string;
  teamId: string;
}): string {
  const scopeId = trimOptional(options.scopeId);
  const teamId = trimOptional(options.teamId);
  if (!scopeId || !teamId) {
    return buildTeamsHref();
  }

  const memberId = trimOptional(options.memberId);
  if (!memberId) {
    return buildTeamDetailHref({
      scopeId,
      tab: 'members',
      teamId,
    });
  }

  return `/scopes/${encodeURIComponent(scopeId)}/teams/${encodeURIComponent(teamId)}/members/${encodeURIComponent(memberId)}/invoke`;
}

export function buildTeamMemberPublishedRunsHref(options: {
  actorId?: string;
  memberId?: string;
  runId?: string;
  scheduleId?: string;
  scopeId: string;
  teamId: string;
}): string {
  const scopeId = trimOptional(options.scopeId);
  const teamId = trimOptional(options.teamId);
  if (!scopeId || !teamId) {
    return buildTeamsHref();
  }

  const memberId = trimOptional(options.memberId);
  if (!memberId) {
    return buildTeamDetailHref({
      scopeId,
      tab: 'members',
      teamId,
    });
  }

  return buildHref(
    `/scopes/${encodeURIComponent(scopeId)}/teams/${encodeURIComponent(teamId)}/members/${encodeURIComponent(memberId)}/runs`,
    {
      runId: options.runId,
      actorId: options.actorId,
      scheduleId: options.scheduleId,
    },
  );
}

export function buildTeamMemberAutomationsHref(options: {
  memberId?: string;
  scopeId: string;
  teamId: string;
}): string {
  const scopeId = trimOptional(options.scopeId);
  const teamId = trimOptional(options.teamId);
  if (!scopeId || !teamId) {
    return buildTeamsHref();
  }

  const memberId = trimOptional(options.memberId);
  if (!memberId) {
    return buildTeamDetailHref({
      scopeId,
      tab: 'automations',
      teamId,
    });
  }

  return `/scopes/${encodeURIComponent(scopeId)}/teams/${encodeURIComponent(teamId)}/members/${encodeURIComponent(memberId)}/automations`;
}

export function readTeamDetailRouteState(
  search = typeof window === 'undefined' ? '' : window.location.search,
  pathname = typeof window === 'undefined' ? '' : window.location.pathname,
): TeamDetailRouteState {
  const params = new URLSearchParams(search);
  const pathnameSegments = pathname.split('/').filter(Boolean);
  const hasScopedTeamPath =
    pathnameSegments[0] === 'scopes' && pathnameSegments[2] === 'teams';
  const scopedTeamsIndex = hasScopedTeamPath ? 2 : -1;
  const scopedMembersIndex =
    scopedTeamsIndex >= 0
      ? pathnameSegments.indexOf('members', scopedTeamsIndex + 2)
      : -1;
  const membersIndex = scopedMembersIndex;
  let scopeIdFromPath = '';
  if (hasScopedTeamPath && pathnameSegments[1]) {
    scopeIdFromPath = decodePathSegment(pathnameSegments[1]);
  }

  let teamIdFromPath = '';
  if (scopedTeamsIndex >= 0 && pathnameSegments[scopedTeamsIndex + 1]) {
    teamIdFromPath = decodePathSegment(pathnameSegments[scopedTeamsIndex + 1]);
  }

  const memberIdFromPath =
    membersIndex >= 0 && pathnameSegments[membersIndex + 1]
      ? decodePathSegment(pathnameSegments[membersIndex + 1])
      : '';
  const defaultTab: TeamDetailTab = 'overview';
  const routeMemberId = memberIdFromPath === 'new' ? '' : memberIdFromPath;
  const memberId = routeMemberId || trimOptional(params.get('memberId'));
  const memberSurfaceFromPath =
    membersIndex >= 0 && pathnameSegments[membersIndex + 2]
      ? decodePathSegment(pathnameSegments[membersIndex + 2])
      : '';
  const pathTab =
    memberSurfaceFromPath === 'automations' ? 'automations' : undefined;

  return {
    memberId,
    routeMemberId,
    runId: trimOptional(params.get('runId')),
    scopeId: scopeIdFromPath || trimOptional(params.get('scopeId')),
    serviceId: trimOptional(params.get('serviceId')),
    tab: parseTeamTab(pathTab ?? params.get('tab'), defaultTab),
    teamId: teamIdFromPath || trimOptional(params.get('teamId')),
    testTeam: ['1', 'true', 'yes'].includes(trimOptional(params.get('testTeam')).toLowerCase()),
    workflowId: trimOptional(params.get('workflowId')),
  };
}

export type { TeamDetailRouteState, TeamDetailTab };
