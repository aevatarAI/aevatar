import {
  buildStudioWorkflowMemberKey,
  buildStudioRoute,
  buildStudioScriptsWorkspaceRoute,
  buildStudioWorkflowEditorRoute,
  buildStudioWorkflowWorkspaceRoute,
  resolveStudioMemberRouteKey,
} from './navigation';

describe('buildStudioRoute', () => {
  it('returns the base path by default', () => {
    expect(buildStudioRoute()).toBe('/studio');
  });

  it('includes focus, tab, and prompt query params when provided', () => {
    expect(
      buildStudioRoute({
        scopeId: 'scope-1',
        focus: 'template:published_demo',
        tab: 'executions',
        prompt: 'Run the current draft',
      }),
    ).toBe(
      '/studio?scopeId=scope-1&focus=template%3Apublished_demo&tab=executions&prompt=Run+the+current+draft',
    );
  });

  it('ignores the removed blank draft mode when requested', () => {
    expect(
      buildStudioRoute({
        draftMode: 'new',
        tab: 'workflows',
      }),
    ).toBe('/studio?tab=workflows');
  });

  it('ignores legacy create-team route params when opening Studio', () => {
    expect(
      buildStudioRoute({
        teamMode: 'create',
        teamName: '订单助手团队',
        entryName: '订单入口',
        draftMode: 'new',
      }),
    ).toBe('/studio');
  });

  it('ignores the removed create-team draft pointer params', () => {
    expect(
      buildStudioRoute({
        teamMode: 'create',
        teamName: '订单助手团队',
        entryName: '订单入口',
        teamDraftWorkflowId: 'workflow-7',
        teamDraftWorkflowName: 'order-entry-draft',
        focus: 'workflow:workflow-7',
      }),
    ).toBe('/studio?focus=workflow%3Aworkflow-7&tab=studio');
  });

  it('supports the dedicated Studio editor tab', () => {
    expect(
      buildStudioRoute({
        focus: 'workflow:workflow-1',
        tab: 'studio',
      }),
    ).toBe('/studio?focus=workflow%3Aworkflow-1&tab=studio');
  });

  it('supports the typed create-member Studio intent', () => {
    expect(
      buildStudioRoute({
        scopeId: 'scope-1',
        teamId: 't-alpha',
        tab: 'studio',
        intent: 'create-member',
      }),
    ).toBe('/studio?scopeId=scope-1&teamId=t-alpha&tab=studio&intent=create-member');
  });

  it('carries a return target for Team handoffs', () => {
    expect(
      buildStudioRoute({
        scopeId: 'scope-1',
        teamId: 't-alpha',
        memberId: 'member-alpha',
        step: 'build',
        tab: 'studio',
        returnTo: '/teams/scope-1/t-alpha?tab=members',
      }),
    ).toBe(
      '/studio?scopeId=scope-1&teamId=t-alpha&member=member%3Amember-alpha&step=build&tab=studio&returnTo=%2Fteams%2Fscope-1%2Ft-alpha%3Ftab%3Dmembers',
    );
  });

  it('drops invalid Studio intent values', () => {
    expect(
      buildStudioRoute({
        tab: 'studio',
        intent: 'delete-team' as never,
      }),
    ).toBe('/studio?tab=studio');
  });

  it('supports opening the scripts workspace for a specific script', () => {
    expect(
      buildStudioRoute({
        tab: 'scripts',
        focus: 'script:script-1',
      }),
    ).toBe('/studio?focus=script%3Ascript-1&tab=scripts');
  });

  it('supports opening the GAgent build workspace', () => {
    expect(
      buildStudioRoute({
        tab: 'gagents',
      }),
    ).toBe('/studio?tab=gagents');
  });

  it('drops the legacy playground route flag when building Studio routes', () => {
    expect(
      buildStudioRoute({
        draftMode: 'new',
        tab: 'studio',
        prompt: 'Review the current draft',
        legacySource: 'playground',
      }),
    ).toBe('/studio?tab=studio&prompt=Review+the+current+draft');
  });

  it('infers the scripts workspace when only a script id is provided', () => {
    expect(
      buildStudioRoute({
        focus: 'script:script-1',
      }),
    ).toBe('/studio?focus=script%3Ascript-1&tab=scripts');
  });

  it('keeps selected member routing separate from lifecycle steps', () => {
    expect(
      buildStudioRoute({
        scopeId: 'scope-1',
        memberKey: 'workflow:workflow-1',
        step: 'bind',
      }),
    ).toBe('/studio?scopeId=scope-1&member=workflow%3Aworkflow-1&step=bind');
  });

  it('builds dedicated workflow and script workspace routes', () => {
    expect(buildStudioWorkflowWorkspaceRoute({ scopeId: 'scope-1' })).toBe(
      '/studio?scopeId=scope-1&tab=studio',
    );
    expect(
      buildStudioWorkflowWorkspaceRoute({
        scopeId: 'scope-a',
        scopeLabel: '团队 A',
        memberId: 'member-alpha',
        memberLabel: '默认成员',
      }),
    ).toBe('/studio?scopeId=scope-a&member=member%3Amember-alpha&tab=studio');
    expect(
      buildStudioWorkflowEditorRoute({
        scopeId: 'scope-1',
        workflowId: 'workflow-1',
      }),
    ).toBe('/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=studio');
    expect(
      buildStudioWorkflowEditorRoute({
        scopeId: 'scope-1',
        memberKey: 'workflow:workflow-1',
        workflowId: 'workflow-1',
      }),
    ).toBe('/studio?scopeId=scope-1&member=workflow%3Aworkflow-1&tab=studio');
    expect(
      buildStudioWorkflowEditorRoute({
        scopeId: 'scope-1',
        memberKey: buildStudioWorkflowMemberKey({
          workflowId: 'default',
          workflowName: 'draft2',
          fileName: 'draft2.yaml',
        }),
        workflowId: 'default',
      }),
    ).toBe('/studio?scopeId=scope-1&member=workflow%3Adraft2&tab=studio');
    expect(
      buildStudioScriptsWorkspaceRoute({
        scopeId: 'scope-1',
        scriptId: 'script-1',
      }),
    ).toBe('/studio?scopeId=scope-1&focus=script%3Ascript-1&tab=scripts');
    expect(
      buildStudioScriptsWorkspaceRoute({
        scopeId: 'scope-1',
        memberId: 'script-member',
        scriptId: 'script-1',
      }),
    ).toBe('/studio?scopeId=scope-1&member=member%3Ascript-member&focus=script%3Ascript-1&tab=scripts');
    expect(
      buildStudioScriptsWorkspaceRoute({
        scopeId: 'scope-1',
        memberKey: 'script:script-1',
        scriptId: 'script-1',
      }),
    ).toBe('/studio?scopeId=scope-1&focus=script%3Ascript-1&tab=scripts');
  });

  it('infers the workflow editor when only a workflow id is provided', () => {
    expect(
      buildStudioRoute({
        focus: 'workflow:workflow-1',
      }),
    ).toBe('/studio?focus=workflow%3Aworkflow-1&tab=studio');
  });

  it('infers the execution view when only an execution id is provided', () => {
    expect(
      buildStudioRoute({
        executionId: 'execution-1',
      }),
    ).toBe('/studio?tab=executions&execution=execution-1');
  });

  it('keeps scope context while honoring deep-link tab priority', () => {
    expect(
      buildStudioRoute({
        scopeId: 'scope-1',
        focus: 'workflow:workflow-1',
        executionId: 'execution-1',
      }),
    ).toBe(
      '/studio?scopeId=scope-1&focus=workflow%3Aworkflow-1&tab=executions&execution=execution-1',
    );
  });

  it('only persists stable scope and member ids in Studio routes', () => {
    expect(
      buildStudioRoute({
        scopeId: 'scope-a',
        scopeLabel: '团队 A',
        memberId: 'member-alpha',
        memberLabel: '成员 Alpha',
        focus: 'workflow:workflow-1',
      }),
    ).toBe(
      '/studio?scopeId=scope-a&member=member%3Amember-alpha&focus=workflow%3Aworkflow-1&tab=studio',
    );
  });
});

describe('resolveStudioMemberRouteKey', () => {
  it('prefers backend member identity over implementation identities', () => {
    expect(
      resolveStudioMemberRouteKey({
        memberId: 'member-alpha',
        memberKey: 'workflow:workflow-1',
        workflowId: 'workflow-2',
        scriptId: 'script-1',
      }),
    ).toBe('member:member-alpha');
  });

  it('keeps an existing member key before falling back to workflow assets', () => {
    expect(
      resolveStudioMemberRouteKey({
        memberKey: 'workflow:workflow-1',
        scriptId: 'script-1',
      }),
    ).toBe('workflow:workflow-1');
    expect(resolveStudioMemberRouteKey({ workflowId: 'workflow-2' })).toBe(
      'workflow:workflow-2',
    );
    expect(resolveStudioMemberRouteKey({ scriptId: 'script-1' })).toBeUndefined();
  });

  it('returns undefined when no stable route identity is available', () => {
    expect(resolveStudioMemberRouteKey({ memberId: ' ', memberKey: 'unknown' })).toBeUndefined();
  });
});
