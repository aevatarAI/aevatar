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
        workflowId: 'workspace-demo',
        template: 'published_demo',
        tab: 'executions',
        prompt: 'Run the current draft',
      }),
    ).toBe(
      '/studio?workflow=workspace-demo&template=published_demo&tab=executions&prompt=Run+the+current+draft',
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
    expect(buildStudioWorkflowWorkspaceRoute()).toBe('/studio?tab=workflows');
    expect(
      buildStudioWorkflowEditorRoute({
        workflowId: 'workflow-1',
      }),
    ).toBe('/studio?workflow=workflow-1&tab=studio');
    expect(
      buildStudioScriptsWorkspaceRoute({
        scriptId: 'script-1',
      }),
    ).toBe('/studio?script=script-1&tab=scripts');
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
});
