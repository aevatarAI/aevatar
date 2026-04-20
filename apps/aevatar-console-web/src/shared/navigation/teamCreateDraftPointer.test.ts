import {
  countTeamCreateDraftPointersOutsideScope,
  findTeamCreateDraftPointer,
  loadAllTeamCreateDraftPointers,
  loadTeamCreateDraftPointer,
  loadTeamCreateDraftPointers,
  resetTeamCreateDraftPointer,
  saveTeamCreateDraftPointer,
  selectTeamCreateDraftPointer,
} from './teamCreateDraftPointer';

describe('teamCreateDraftPointer', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it('keeps multiple saved drafts instead of overwriting the previous one', () => {
    saveTeamCreateDraftPointer({
      scopeId: 'scope-a',
      teamName: '测试',
      entryName: '测试',
      teamDraftWorkflowId: 'joker',
      teamDraftWorkflowName: 'joker',
    });
    saveTeamCreateDraftPointer({
      scopeId: 'scope-a',
      teamName: '订单助手',
      entryName: '订单助手',
      teamDraftWorkflowId: 'test03',
      teamDraftWorkflowName: 'test03',
    });

    expect(loadTeamCreateDraftPointers()).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          teamDraftWorkflowId: 'joker',
          teamDraftWorkflowName: 'joker',
        }),
        expect.objectContaining({
          teamDraftWorkflowId: 'test03',
          teamDraftWorkflowName: 'test03',
        }),
      ]),
    );
    expect(loadTeamCreateDraftPointers()).toHaveLength(2);
    expect(loadTeamCreateDraftPointer()).toEqual(
      expect.objectContaining({
        teamDraftWorkflowId: 'test03',
      }),
    );
  });

  it('can switch the selected resume draft without removing the others', () => {
    saveTeamCreateDraftPointer({
      scopeId: 'scope-a',
      teamName: '测试',
      entryName: '测试',
      teamDraftWorkflowId: 'joker',
      teamDraftWorkflowName: 'joker',
    });
    saveTeamCreateDraftPointer({
      scopeId: 'scope-a',
      teamName: '订单助手',
      entryName: '订单助手',
      teamDraftWorkflowId: 'test03',
      teamDraftWorkflowName: 'test03',
    });

    selectTeamCreateDraftPointer('joker');

    expect(loadTeamCreateDraftPointer()).toEqual(
      expect.objectContaining({
        teamDraftWorkflowId: 'joker',
      }),
    );
    expect(loadTeamCreateDraftPointers()).toHaveLength(2);
  });

  it('keeps the source behavior definition on the saved team draft pointer', () => {
    saveTeamCreateDraftPointer({
      scopeId: 'scope-a',
      teamName: '订单测试',
      entryName: '订单测试',
      teamDraftWorkflowId: 'team-draft-1',
      teamDraftWorkflowName: '订单测试',
      sourceBehaviorDefinitionId: 'workflow-hello-chat',
      sourceBehaviorDefinitionName: 'hello-chat',
    });

    expect(findTeamCreateDraftPointer('team-draft-1')).toEqual(
      expect.objectContaining({
        sourceBehaviorDefinitionId: 'workflow-hello-chat',
        sourceBehaviorDefinitionName: 'hello-chat',
      }),
    );
  });

  it('removes only the requested draft pointer when workflow id is provided', () => {
    saveTeamCreateDraftPointer({
      scopeId: 'scope-a',
      teamName: '测试',
      entryName: '测试',
      teamDraftWorkflowId: 'joker',
      teamDraftWorkflowName: 'joker',
    });
    saveTeamCreateDraftPointer({
      scopeId: 'scope-a',
      teamName: '订单助手',
      entryName: '订单助手',
      teamDraftWorkflowId: 'test03',
      teamDraftWorkflowName: 'test03',
    });

    resetTeamCreateDraftPointer('test03');

    expect(loadTeamCreateDraftPointers()).toEqual([
      expect.objectContaining({
        teamDraftWorkflowId: 'joker',
      }),
    ]);
    expect(loadTeamCreateDraftPointer()).toEqual(
      expect.objectContaining({
        teamDraftWorkflowId: 'joker',
      }),
    );
  });

  it('isolates draft pointers by scope when loading, selecting, and resetting', () => {
    saveTeamCreateDraftPointer({
      scopeId: 'scope-a',
      teamName: '范围 A',
      entryName: '范围 A',
      teamDraftWorkflowId: 'shared-draft',
      teamDraftWorkflowName: 'shared-draft',
    });
    saveTeamCreateDraftPointer({
      scopeId: 'scope-b',
      teamName: '范围 B',
      entryName: '范围 B',
      teamDraftWorkflowId: 'shared-draft',
      teamDraftWorkflowName: 'shared-draft',
    });
    saveTeamCreateDraftPointer({
      scopeId: 'scope-a',
      teamName: '范围 A 二号',
      entryName: '范围 A 二号',
      teamDraftWorkflowId: 'scope-a-draft',
      teamDraftWorkflowName: 'scope-a-draft',
    });

    expect(loadTeamCreateDraftPointers('scope-a')).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          scopeId: 'scope-a',
          teamDraftWorkflowId: 'shared-draft',
        }),
        expect.objectContaining({
          scopeId: 'scope-a',
          teamDraftWorkflowId: 'scope-a-draft',
        }),
      ]),
    );
    expect(loadTeamCreateDraftPointers('scope-a')).toHaveLength(2);
    expect(loadTeamCreateDraftPointers('scope-b')).toEqual([
      expect.objectContaining({
        scopeId: 'scope-b',
        teamDraftWorkflowId: 'shared-draft',
      }),
    ]);
    expect(loadAllTeamCreateDraftPointers()).toHaveLength(3);
    expect(countTeamCreateDraftPointersOutsideScope('scope-a')).toBe(1);
    expect(countTeamCreateDraftPointersOutsideScope('scope-b')).toBe(2);

    selectTeamCreateDraftPointer('shared-draft', 'scope-a');
    expect(loadTeamCreateDraftPointer('scope-a')).toEqual(
      expect.objectContaining({
        scopeId: 'scope-a',
        teamDraftWorkflowId: 'shared-draft',
      }),
    );
    expect(loadTeamCreateDraftPointer('scope-b')).toEqual(
      expect.objectContaining({
        scopeId: 'scope-b',
        teamDraftWorkflowId: 'shared-draft',
      }),
    );

    resetTeamCreateDraftPointer('shared-draft', 'scope-a');
    expect(loadTeamCreateDraftPointers('scope-a')).toEqual([
      expect.objectContaining({
        scopeId: 'scope-a',
        teamDraftWorkflowId: 'scope-a-draft',
      }),
    ]);
    expect(loadTeamCreateDraftPointers('scope-b')).toEqual([
      expect.objectContaining({
        scopeId: 'scope-b',
        teamDraftWorkflowId: 'shared-draft',
      }),
    ]);
  });
});
