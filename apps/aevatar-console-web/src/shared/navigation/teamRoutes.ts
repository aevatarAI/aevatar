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

type TeamMemberDraftWorkflowHintInput = {
  readonly memberId?: string | null;
  readonly publishedServiceId?: string | null;
  readonly routeMemberId?: string | null;
  readonly routeWorkflowId?: string | null;
};

type TeamMemberDraftWorkflowHintStorageInput = {
  readonly memberId?: string | null;
  readonly publishedServiceId?: string | null;
  readonly scopeId?: string | null;
  readonly teamId?: string | null;
  readonly workflowId?: string | null;
};

type TeamMemberDraftWorkflowHintReadInput =
  TeamMemberDraftWorkflowHintInput & {
    readonly scopeId?: string | null;
    readonly teamId?: string | null;
  };

const teamMemberDraftWorkflowHintStoragePrefix =
  'aevatar.teamMemberDraftWorkflowHint.v1';

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

function isWorkflowDraftHintValue(value: string): boolean {
  return Boolean(value && !/\s/.test(value));
}

function isPublishedServiceWorkflowIdentity(input: {
  readonly memberId?: string | null;
  readonly publishedServiceId?: string | null;
  readonly workflowId?: string | null;
}): boolean {
  const workflowId = trimOptional(input.workflowId);
  if (!workflowId) {
    return false;
  }

  const publishedServiceId = trimOptional(input.publishedServiceId);
  if (publishedServiceId && workflowId === publishedServiceId) {
    return true;
  }

  const memberId = trimOptional(input.memberId);
  return Boolean(memberId && workflowId === `member-${memberId}`);
}

function buildTeamMemberDraftWorkflowHintStorageKey(input: {
  readonly memberId: string;
  readonly scopeId: string;
  readonly teamId: string;
}): string {
  return [
    teamMemberDraftWorkflowHintStoragePrefix,
    encodeURIComponent(input.scopeId),
    encodeURIComponent(input.teamId),
    encodeURIComponent(input.memberId),
  ].join(':');
}

function getSessionStorage(): Storage | null {
  if (typeof window === 'undefined') {
    return null;
  }

  try {
    return window.sessionStorage ?? null;
  } catch {
    return null;
  }
}

export function resolveTeamMemberDraftWorkflowHint(
  input: TeamMemberDraftWorkflowHintInput,
): string {
  const memberId = trimOptional(input.memberId);
  const routeMemberId = trimOptional(input.routeMemberId);
  const routeWorkflowId = trimOptional(input.routeWorkflowId);
  if (
    !memberId ||
    !routeMemberId ||
    memberId !== routeMemberId ||
    !isWorkflowDraftHintValue(routeWorkflowId) ||
    isPublishedServiceWorkflowIdentity({
      memberId,
      publishedServiceId: input.publishedServiceId,
      workflowId: routeWorkflowId,
    })
  ) {
    return "";
  }

  return routeWorkflowId;
}

export function rememberTeamMemberDraftWorkflowHint(
  input: TeamMemberDraftWorkflowHintStorageInput,
): void {
  const scopeId = trimOptional(input.scopeId);
  const teamId = trimOptional(input.teamId);
  const memberId = trimOptional(input.memberId);
  const workflowId = trimOptional(input.workflowId);
  if (
    !scopeId ||
    !teamId ||
    !memberId ||
    !isWorkflowDraftHintValue(workflowId) ||
    isPublishedServiceWorkflowIdentity({
      memberId,
      publishedServiceId: input.publishedServiceId,
      workflowId,
    })
  ) {
    return;
  }

  const storage = getSessionStorage();
  if (!storage) {
    return;
  }

  try {
    storage.setItem(
      buildTeamMemberDraftWorkflowHintStorageKey({ memberId, scopeId, teamId }),
      workflowId,
    );
  } catch {
    // Session storage is only a same-tab route recovery hint. Ignore browsers
    // that make it unavailable; the editor still validates the route workflow id.
  }
}

export function readTeamMemberDraftWorkflowHint(
  input: TeamMemberDraftWorkflowHintReadInput,
): string {
  const routeHint = resolveTeamMemberDraftWorkflowHint(input);
  if (routeHint) {
    return routeHint;
  }

  const scopeId = trimOptional(input.scopeId);
  const teamId = trimOptional(input.teamId);
  const memberId = trimOptional(input.memberId);
  if (!scopeId || !teamId || !memberId) {
    return "";
  }

  const storage = getSessionStorage();
  let storedWorkflowId = "";
  try {
    storedWorkflowId = trimOptional(
      storage?.getItem(
        buildTeamMemberDraftWorkflowHintStorageKey({ memberId, scopeId, teamId }),
      ),
    );
  } catch {
    storedWorkflowId = "";
  }
  if (
    !isWorkflowDraftHintValue(storedWorkflowId) ||
    isPublishedServiceWorkflowIdentity({
      memberId,
      publishedServiceId: input.publishedServiceId,
      workflowId: storedWorkflowId,
    })
  ) {
    return "";
  }

  return storedWorkflowId;
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
  const memberId =
    memberIdFromPath === 'new'
      ? ''
      : memberIdFromPath || trimOptional(params.get('memberId'));

  return {
    memberId,
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
