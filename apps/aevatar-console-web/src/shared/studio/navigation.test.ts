import {
  buildStudioRoute,
  buildStudioScriptsWorkspaceRoute,
  buildStudioWorkflowEditorRoute,
  buildStudioWorkflowWorkspaceRoute,
} from './navigation';

describe('buildStudioRoute', () => {
  it('returns the base path by default', () => {
    expect(buildStudioRoute()).toBe('/studio');
  });

  it('includes workflow, template, tab, and prompt query params when provided', () => {
    expect(
      buildStudioRoute({
        scopeId: 'scope-1',
        workflowId: 'workspace-demo',
        template: 'published_demo',
        tab: 'executions',
        prompt: 'Run the current draft',
      }),
    ).toBe(
      '/studio?scopeId=scope-1&workflow=workspace-demo&template=published_demo&tab=executions&prompt=Run+the+current+draft',
    );
  });

  it('includes the blank draft mode when requested', () => {
    expect(
      buildStudioRoute({
        draftMode: 'new',
        tab: 'workflows',
      }),
    ).toBe('/studio?tab=workflows&draft=new');
  });

  it('supports the dedicated Studio editor tab', () => {
    expect(
      buildStudioRoute({
        workflowId: 'workflow-1',
        tab: 'studio',
      }),
    ).toBe('/studio?workflow=workflow-1&tab=studio');
  });

  it('supports opening the scripts workspace for a specific script', () => {
    expect(
      buildStudioRoute({
        tab: 'scripts',
        scriptId: 'script-1',
      }),
    ).toBe('/studio?script=script-1&tab=scripts');
  });

  it('supports redirecting legacy playground drafts into Studio', () => {
    expect(
      buildStudioRoute({
        draftMode: 'new',
        tab: 'studio',
        prompt: 'Review the current draft',
        legacySource: 'playground',
      }),
    ).toBe(
      '/studio?tab=studio&draft=new&prompt=Review+the+current+draft&legacy=playground',
    );
  });

  it('infers the scripts workspace when only a script id is provided', () => {
    expect(
      buildStudioRoute({
        scriptId: 'script-1',
      }),
    ).toBe('/studio?script=script-1&tab=scripts');
  });

  it('builds dedicated workflow and script workspace routes', () => {
    expect(buildStudioWorkflowWorkspaceRoute({ scopeId: 'scope-1' })).toBe(
      '/studio?scopeId=scope-1&tab=workflows',
    );
    expect(
      buildStudioWorkflowWorkspaceRoute({
        scopeId: 'scope-a',
        scopeLabel: '团队 A',
        memberId: 'service-alpha',
        memberLabel: '默认成员',
      }),
    ).toBe(
      '/studio?scopeId=scope-a&scopeLabel=%E5%9B%A2%E9%98%9F+A&memberId=service-alpha&memberLabel=%E9%BB%98%E8%AE%A4%E6%88%90%E5%91%98&tab=workflows',
    );
    expect(
      buildStudioWorkflowEditorRoute({
        scopeId: 'scope-1',
        workflowId: 'workflow-1',
      }),
    ).toBe('/studio?scopeId=scope-1&workflow=workflow-1&tab=studio');
    expect(
      buildStudioScriptsWorkspaceRoute({
        scopeId: 'scope-1',
        scriptId: 'script-1',
      }),
    ).toBe('/studio?scopeId=scope-1&script=script-1&tab=scripts');
  });

  it('infers the workflow editor when only a workflow id is provided', () => {
    expect(
      buildStudioRoute({
        workflowId: 'workflow-1',
      }),
    ).toBe('/studio?workflow=workflow-1&tab=studio');
  });

  it('infers the execution view when only an execution id is provided', () => {
    expect(
      buildStudioRoute({
        executionId: 'execution-1',
      }),
    ).toBe('/studio?tab=executions&execution=execution-1');
  });

  it('preserves team context query params when building Studio routes', () => {
    expect(
      buildStudioRoute({
        scopeId: 'scope-a',
        scopeLabel: '团队 A',
        memberId: 'service-alpha',
        memberLabel: '成员 Alpha',
        workflowId: 'workflow-1',
      }),
    ).toBe(
      '/studio?scopeId=scope-a&scopeLabel=%E5%9B%A2%E9%98%9F+A&memberId=service-alpha&memberLabel=%E6%88%90%E5%91%98+Alpha&workflow=workflow-1&tab=studio',
    );
  });
});
