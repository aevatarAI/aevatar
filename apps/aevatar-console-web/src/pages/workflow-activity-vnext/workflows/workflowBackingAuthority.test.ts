import { StudioApiError } from '@/shared/studio/api';
import type {
  StudioMemberDetail,
  StudioMemberSummary,
  StudioTeamSummary,
} from '@/shared/studio/models';
import {
  cleanupWorkflowBackingAuthority,
  provisionWorkflowBackingAuthority,
  resolveWorkflowBackingAuthority,
} from '@/shared/studio/workflowBackingAuthority';

function member(
  changes: Partial<StudioMemberSummary> & {
    readonly memberId: string;
    readonly workflowId?: string;
  },
): StudioMemberSummary {
  const { workflowId, ...summaryChanges } = changes;
  return {
    scopeId: 'scope-alpha',
    displayName: 'Approval flow',
    description: '',
    implementationKind: 'workflow',
    implementationRef: workflowId
      ? {
          implementationKind: 'workflow',
          workflowId,
        }
      : null,
    lifecycleStage: 'created',
    publishedServiceId: 'svc-alpha',
    lastBoundRevisionId: null,
    teamId: changes.teamId ?? 't-alpha',
    createdAt: '2026-08-07T00:00:00Z',
    updatedAt: '2026-08-07T00:00:00Z',
    ...summaryChanges,
  };
}

function memberDetail(summary: StudioMemberSummary): StudioMemberDetail {
  return {
    summary,
    implementationRef: summary.implementationRef,
    lastBinding: null,
    currentBindingRun: null,
  };
}

function team(): StudioTeamSummary {
  return {
    teamId: 't-alpha',
    scopeId: 'scope-alpha',
    displayName: 'Approval flow',
    description: '',
    lifecycleStage: 'active',
    memberCount: 0,
    createdAt: '2026-08-07T00:00:00Z',
    updatedAt: '2026-08-07T00:00:00Z',
  };
}

describe('resolveWorkflowBackingAuthority', () => {
  it('resolves only the member with the exact typed workflow reference', () => {
    expect(
      resolveWorkflowBackingAuthority({
        workflowId: 'wf-alpha',
        members: [
          member({ memberId: 'm-other', workflowId: 'wf-other' }),
          member({
            memberId: 'm-alpha',
            teamId: 't-alpha',
            workflowId: 'wf-alpha',
          }),
        ],
      }),
    ).toEqual({ memberId: 'm-alpha', teamId: 't-alpha' });
  });

  it('returns null when no typed workflow relationship exists', () => {
    expect(
      resolveWorkflowBackingAuthority({
        workflowId: 'wf-alpha',
        members: [member({ memberId: 'wf-alpha' })],
      }),
    ).toBeNull();
  });

  it('rejects duplicate authorities instead of selecting one', () => {
    expect(() =>
      resolveWorkflowBackingAuthority({
        workflowId: 'wf-alpha',
        members: [
          member({ memberId: 'm-alpha', workflowId: 'wf-alpha' }),
          member({
            memberId: 'm-duplicate',
            teamId: 't-duplicate',
            workflowId: 'wf-alpha',
          }),
        ],
      }),
    ).toThrow('more than one backing authority');
  });
});

describe('provisionWorkflowBackingAuthority', () => {
  it('creates one Team and Member, then persists their typed workflow relationship', async () => {
    const createdMember = member({ memberId: 'm-alpha' });
    const linkedMember = member({
      memberId: 'm-alpha',
      workflowId: 'wf-alpha',
    });
    const calls: string[] = [];
    const api = {
      createTeam: jest.fn(async () => {
        calls.push('createTeam');
        return team();
      }),
      getTeam: jest.fn(async () => {
        calls.push('getTeam');
        return team();
      }),
      createMember: jest.fn(async () => {
        calls.push('createMember');
        return createdMember;
      }),
      getMember: jest
        .fn()
        .mockImplementationOnce(async () => {
          calls.push('getMember');
          return memberDetail(createdMember);
        })
        .mockImplementationOnce(async () => {
          calls.push('getMemberLinked');
          return memberDetail(linkedMember);
        }),
      listMembers: jest.fn(async () => {
        calls.push('listMembers');
        return { scopeId: 'scope-alpha', members: [] };
      }),
      updateMemberImplementationRef: jest.fn(async () => {
        calls.push('linkMember');
        return {
          status: 'accepted' as const,
          scopeId: 'scope-alpha',
          memberId: 'm-alpha',
        };
      }),
    };

    await expect(
      provisionWorkflowBackingAuthority({
        api,
        scopeId: 'scope-alpha',
        workflowId: 'wf-alpha',
        workflowName: 'Approval flow',
        wait: async () => undefined,
      }),
    ).resolves.toEqual({ memberId: 'm-alpha', teamId: 't-alpha' });

    expect(calls).toEqual([
      'listMembers',
      'createTeam',
      'getTeam',
      'createMember',
      'getMember',
      'linkMember',
      'getMemberLinked',
    ]);
    expect(api.createMember).toHaveBeenCalledWith({
      scopeId: 'scope-alpha',
      displayName: 'Approval flow',
      implementationKind: 'workflow',
      teamId: 't-alpha',
    });
    expect(api.updateMemberImplementationRef).toHaveBeenCalledWith({
      scopeId: 'scope-alpha',
      memberId: 'm-alpha',
      implementationRef: {
        implementationKind: 'workflow',
        workflowId: 'wf-alpha',
      },
    });
  });

  it('reuses an existing typed relationship without creating resources', async () => {
    const linkedMember = member({
      memberId: 'm-alpha',
      workflowId: 'wf-alpha',
    });
    const api = {
      createTeam: jest.fn(),
      getTeam: jest.fn(),
      createMember: jest.fn(),
      getMember: jest.fn(),
      listMembers: jest.fn(async () => ({
        scopeId: 'scope-alpha',
        members: [linkedMember],
      })),
      updateMemberImplementationRef: jest.fn(),
    };

    await expect(
      provisionWorkflowBackingAuthority({
        api,
        scopeId: 'scope-alpha',
        workflowId: 'wf-alpha',
        workflowName: 'Approval flow',
        wait: async () => undefined,
      }),
    ).resolves.toEqual({ memberId: 'm-alpha', teamId: 't-alpha' });
    expect(api.createTeam).not.toHaveBeenCalled();
    expect(api.createMember).not.toHaveBeenCalled();
  });

  it('retries read-model 404s without creating another identity', async () => {
    const notFound = new StudioApiError('Not Found', 404);
    const createdMember = member({ memberId: 'm-alpha' });
    const linkedMember = member({
      memberId: 'm-alpha',
      workflowId: 'wf-alpha',
    });
    const api = {
      createTeam: jest.fn(async () => team()),
      getTeam: jest
        .fn()
        .mockRejectedValueOnce(notFound)
        .mockResolvedValue(team()),
      createMember: jest.fn(async () => createdMember),
      getMember: jest
        .fn()
        .mockRejectedValueOnce(notFound)
        .mockResolvedValueOnce(memberDetail(createdMember))
        .mockResolvedValueOnce(memberDetail(linkedMember)),
      listMembers: jest.fn(async () => ({
        scopeId: 'scope-alpha',
        members: [],
      })),
      updateMemberImplementationRef: jest.fn(async () => ({
        status: 'accepted' as const,
        scopeId: 'scope-alpha',
        memberId: 'm-alpha',
      })),
    };

    await provisionWorkflowBackingAuthority({
      api,
      scopeId: 'scope-alpha',
      workflowId: 'wf-alpha',
      workflowName: 'Approval flow',
      wait: async () => undefined,
    });

    expect(api.createTeam).toHaveBeenCalledTimes(1);
    expect(api.createMember).toHaveBeenCalledTimes(1);
    expect(api.getTeam).toHaveBeenCalledTimes(2);
    expect(api.getMember).toHaveBeenCalledTimes(3);
  });
});

describe('cleanupWorkflowBackingAuthority', () => {
  it('deletes only the exact typed Member, its Team, and then the Workflow draft', async () => {
    const calls: string[] = [];
    const api = {
      listMembers: jest.fn(async () => ({
        scopeId: 'scope-alpha',
        members: [
          member({
            memberId: 'm-other',
            teamId: 't-other',
            workflowId: 'wf-other',
          }),
          member({
            memberId: 'm-alpha',
            teamId: 't-alpha',
            workflowId: 'wf-alpha',
          }),
        ],
      })),
      deleteMember: jest.fn(async ({ memberId }) => {
        calls.push(`deleteMember:${memberId}`);
        return {
          status: 'delete_accepted' as const,
          scopeId: 'scope-alpha',
          memberId,
        };
      }),
      archiveTeam: jest.fn(async (_scopeId, teamId) => {
        calls.push(`archiveTeam:${teamId}`);
        return {
          status: 'accepted' as const,
          scopeId: 'scope-alpha',
          teamId,
        };
      }),
      deleteWorkflowDraft: jest.fn(async (workflowId) => {
        calls.push(`deleteWorkflowDraft:${workflowId}`);
      }),
    };

    await expect(
      cleanupWorkflowBackingAuthority({
        api,
        scopeId: 'scope-alpha',
        workflowId: 'wf-alpha',
      }),
    ).resolves.toEqual({ memberId: 'm-alpha', teamId: 't-alpha' });
    expect(calls).toEqual([
      'deleteMember:m-alpha',
      'archiveTeam:t-alpha',
      'deleteWorkflowDraft:wf-alpha',
    ]);
    expect(api.deleteMember).not.toHaveBeenCalledWith(
      expect.objectContaining({ memberId: 'm-other' }),
    );
    expect(api.archiveTeam).not.toHaveBeenCalledWith('scope-alpha', 't-other');
  });

  it('treats already-cleaned Member, Team, and draft 404 responses as success', async () => {
    const notFound = new StudioApiError('Not Found', 404);
    const api = {
      listMembers: jest.fn(async () => ({
        scopeId: 'scope-alpha',
        members: [],
      })),
      deleteMember: jest.fn().mockRejectedValue(notFound),
      archiveTeam: jest.fn().mockRejectedValue(notFound),
      deleteWorkflowDraft: jest.fn().mockRejectedValue(notFound),
    };

    await expect(
      cleanupWorkflowBackingAuthority({
        api,
        authority: { memberId: 'm-alpha', teamId: 't-alpha' },
        scopeId: 'scope-alpha',
        workflowId: 'wf-alpha',
      }),
    ).resolves.toEqual({ memberId: 'm-alpha', teamId: 't-alpha' });
  });
});
