import { buildStudioRoute } from './navigation';

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
});
