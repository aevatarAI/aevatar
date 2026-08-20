import {
  buildWorkflowActivityNewHref,
  buildWorkflowActivityTemplatesHref,
} from './navigation';

describe('Workflow Activity vNext navigation', () => {
  it('builds an encoded canonical template creation URL', () => {
    expect(buildWorkflowActivityTemplatesHref('scope with space')).toBe(
      '/scopes/scope%20with%20space/workflow-activity-vnext/workflows/new/templates',
    );
  });

  it('keeps the template change-method destination on the new workflow route', () => {
    expect(buildWorkflowActivityNewHref('scope-alpha')).toBe(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/new',
    );
  });
});
