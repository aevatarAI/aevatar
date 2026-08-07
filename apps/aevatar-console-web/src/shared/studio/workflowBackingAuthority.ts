import { t } from '@/shared/i18n/messages';
import { isStudioApiStatus, studioApi } from '@/shared/studio/api';
import type {
  StudioMemberDetail,
  StudioMemberSummary,
  StudioTeamSummary,
} from '@/shared/studio/models';

const MATERIALIZATION_ATTEMPTS = 8;
const MATERIALIZATION_DELAY_MS = 300;

export type WorkflowBackingAuthority = {
  readonly memberId: string;
  readonly teamId: string;
};

export type WorkflowBackingAuthorityApi = Pick<
  typeof studioApi,
  | 'createMember'
  | 'createTeam'
  | 'getMember'
  | 'getTeam'
  | 'listMembers'
  | 'updateMemberImplementationRef'
>;

export type WorkflowBackingAuthorityCleanupApi = Pick<
  typeof studioApi,
  'archiveTeam' | 'deleteMember' | 'deleteWorkflowDraft' | 'listMembers'
>;

type Wait = (delayMs: number) => Promise<void>;

function trim(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function defaultWait(delayMs: number): Promise<void> {
  return new Promise((resolve) => window.setTimeout(resolve, delayMs));
}

function memberWorkflowId(member: StudioMemberSummary): string {
  if (
    member.implementationKind !== 'workflow' ||
    member.implementationRef?.implementationKind !== 'workflow'
  ) {
    return '';
  }

  return trim(member.implementationRef.workflowId);
}

function toAuthority(member: StudioMemberSummary): WorkflowBackingAuthority {
  const memberId = trim(member.memberId);
  const teamId = trim(member.teamId);
  if (!memberId || !teamId) {
    throw new Error(
      'The Workflow backing authority is missing its Member or Team identity.',
    );
  }

  return { memberId, teamId };
}

export function resolveWorkflowBackingAuthority(input: {
  readonly members: readonly StudioMemberSummary[];
  readonly workflowId: string;
}): WorkflowBackingAuthority | null {
  const workflowId = trim(input.workflowId);
  if (!workflowId) {
    throw new Error(
      'A Workflow identity is required to resolve its authority.',
    );
  }

  const matches = input.members.filter(
    (member) => memberWorkflowId(member) === workflowId,
  );
  if (matches.length > 1) {
    throw new Error(
      `Workflow '${workflowId}' has more than one backing authority.`,
    );
  }

  return matches[0] ? toAuthority(matches[0]) : null;
}

async function pollReadable<T>(input: {
  readonly read: () => Promise<T>;
  readonly accept: (value: T) => boolean;
  readonly wait: Wait;
  readonly unavailableMessage: string;
}): Promise<T> {
  let lastError: unknown = null;
  for (let attempt = 0; attempt < MATERIALIZATION_ATTEMPTS; attempt += 1) {
    try {
      const value = await input.read();
      if (input.accept(value)) return value;
    } catch (error) {
      if (!isStudioApiStatus(error, 404)) throw error;
      lastError = error;
    }

    if (attempt < MATERIALIZATION_ATTEMPTS - 1) {
      await input.wait(MATERIALIZATION_DELAY_MS);
    }
  }

  throw new Error(
    input.unavailableMessage,
    lastError instanceof Error ? { cause: lastError } : undefined,
  );
}

async function waitForWorkflowTeamVisible(input: {
  readonly api: WorkflowBackingAuthorityApi;
  readonly scopeId: string;
  readonly teamId: string;
  readonly wait: Wait;
}): Promise<StudioTeamSummary> {
  return pollReadable({
    read: () => input.api.getTeam(input.scopeId, input.teamId),
    accept: (team) =>
      trim(team.scopeId) === input.scopeId &&
      trim(team.teamId) === input.teamId,
    wait: input.wait,
    unavailableMessage:
      'The Workflow authority Team was accepted but is not readable yet.',
  });
}

export async function waitForWorkflowMemberVisible(input: {
  readonly api: WorkflowBackingAuthorityApi;
  readonly memberId: string;
  readonly scopeId: string;
  readonly wait?: Wait;
}): Promise<StudioMemberDetail> {
  const wait = input.wait ?? defaultWait;
  return pollReadable({
    read: () => input.api.getMember(input.scopeId, input.memberId),
    accept: (member) =>
      trim(member.summary.scopeId) === input.scopeId &&
      trim(member.summary.memberId) === input.memberId,
    wait,
    unavailableMessage:
      'The Workflow authority Member was accepted but is not readable yet.',
  });
}

export async function linkWorkflowMemberDraft(input: {
  readonly api: WorkflowBackingAuthorityApi;
  readonly memberId: string;
  readonly scopeId: string;
  readonly workflowId: string;
  readonly wait?: Wait;
}): Promise<void> {
  const wait = input.wait ?? defaultWait;
  await waitForWorkflowMemberVisible({ ...input, wait });

  try {
    await input.api.updateMemberImplementationRef({
      scopeId: input.scopeId,
      memberId: input.memberId,
      implementationRef: {
        implementationKind: 'workflow',
        workflowId: input.workflowId,
      },
    });
  } catch (error) {
    if (!isStudioApiStatus(error, 404)) throw error;
    await waitForWorkflowMemberVisible({ ...input, wait });
    await input.api.updateMemberImplementationRef({
      scopeId: input.scopeId,
      memberId: input.memberId,
      implementationRef: {
        implementationKind: 'workflow',
        workflowId: input.workflowId,
      },
    });
  }
}

async function waitForWorkflowMemberLinked(input: {
  readonly api: WorkflowBackingAuthorityApi;
  readonly memberId: string;
  readonly scopeId: string;
  readonly workflowId: string;
  readonly wait: Wait;
}): Promise<StudioMemberDetail> {
  return pollReadable({
    read: () => input.api.getMember(input.scopeId, input.memberId),
    accept: (member) =>
      trim(member.summary.memberId) === input.memberId &&
      memberWorkflowId(member.summary) === input.workflowId,
    wait: input.wait,
    unavailableMessage:
      'The Workflow authority Member link was accepted but is not readable yet.',
  });
}

export async function provisionWorkflowBackingAuthority(input: {
  readonly api?: WorkflowBackingAuthorityApi;
  readonly scopeId: string;
  readonly workflowId: string;
  readonly workflowName: string;
  readonly wait?: Wait;
}): Promise<WorkflowBackingAuthority> {
  const api = input.api ?? studioApi;
  const scopeId = trim(input.scopeId);
  const workflowId = trim(input.workflowId);
  const workflowName = trim(input.workflowName);
  const wait = input.wait ?? defaultWait;
  if (!scopeId || !workflowId || !workflowName) {
    throw new Error(
      'Scope, Workflow identity, and Workflow name are required for authority provisioning.',
    );
  }

  const roster = await api.listMembers(scopeId);
  const existing = resolveWorkflowBackingAuthority({
    members: roster.members,
    workflowId,
  });
  if (existing) return existing;

  const createdTeam = await api.createTeam({
    scopeId,
    displayName: workflowName,
    description: t(
      'workflowActivityVNext.new.backingAuthorityDescription',
      'System-managed authority for one Workflow.',
    ),
  });
  const teamId = trim(createdTeam.teamId);
  if (!teamId) {
    throw new Error('Workflow Team creation did not return a stable identity.');
  }
  await waitForWorkflowTeamVisible({ api, scopeId, teamId, wait });

  const createdMember = await api.createMember({
    scopeId,
    displayName: workflowName,
    implementationKind: 'workflow',
    teamId,
  });
  const memberId = trim(createdMember.memberId);
  if (!memberId) {
    throw new Error(
      'Workflow Member creation did not return a stable identity.',
    );
  }

  await linkWorkflowMemberDraft({
    api,
    scopeId,
    memberId,
    workflowId,
    wait,
  });
  await waitForWorkflowMemberLinked({
    api,
    scopeId,
    memberId,
    workflowId,
    wait,
  });

  return { memberId, teamId };
}

async function ignoreAlreadyCleaned(operation: () => Promise<unknown>) {
  try {
    await operation();
  } catch (error) {
    if (!isStudioApiStatus(error, 404)) throw error;
  }
}

export async function cleanupWorkflowBackingAuthority(input: {
  readonly api?: WorkflowBackingAuthorityCleanupApi;
  readonly authority?: WorkflowBackingAuthority | null;
  readonly scopeId: string;
  readonly workflowId: string;
}): Promise<WorkflowBackingAuthority | null> {
  const api = input.api ?? studioApi;
  const scopeId = trim(input.scopeId);
  const workflowId = trim(input.workflowId);
  if (!scopeId || !workflowId) {
    throw new Error(
      'Scope and Workflow identity are required for authority cleanup.',
    );
  }

  const authority =
    input.authority === undefined
      ? resolveWorkflowBackingAuthority({
          members: (await api.listMembers(scopeId)).members,
          workflowId,
        })
      : input.authority;

  if (authority) {
    await ignoreAlreadyCleaned(() =>
      api.deleteMember({ scopeId, memberId: authority.memberId }),
    );
    await ignoreAlreadyCleaned(() =>
      api.archiveTeam(scopeId, authority.teamId),
    );
  }
  await ignoreAlreadyCleaned(() =>
    api.deleteWorkflowDraft(workflowId, scopeId),
  );

  return authority;
}
