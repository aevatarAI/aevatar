import {
  buildStudioWorkflowMemberKey,
  buildStudioRoute,
  buildStudioScriptsWorkspaceRoute,
  buildStudioWorkflowEditorRoute,
  buildStudioWorkflowWorkspaceRoute,
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
        tab: 'studio',
        intent: 'create-member',
      }),
    ).toBe('/studio?tab=studio&intent=create-member');
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
    ).toBe('/studio?scopeId=scope-1&member=workflow%3Aworkflow-1&step=bind&tab=bindings');
  });

  it('builds dedicated workflow and script workspace routes', () => {
    expect(buildStudioWorkflowWorkspaceRoute({ scopeId: 'scope-1' })).toBe(
      '/studio?scopeId=scope-1&tab=studio',
    );
    expect(
      buildStudioWorkflowWorkspaceRoute({
        scopeId: 'scope-a',
        scopeLabel: '团队 A',
        memberId: 'service-alpha',
        memberLabel: '默认成员',
      }),
    ).toBe('/studio?scopeId=scope-a&member=member%3Aservice-alpha&tab=studio');
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
        memberKey: 'script:script-1',
        scriptId: 'script-1',
      }),
    ).toBe('/studio?scopeId=scope-1&member=script%3Ascript-1&tab=scripts');
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
        memberId: 'service-alpha',
        memberLabel: '成员 Alpha',
        focus: 'workflow:workflow-1',
      }),
    ).toBe(
      '/studio?scopeId=scope-a&member=member%3Aservice-alpha&focus=workflow%3Aworkflow-1&tab=studio',
    );
  });
});
