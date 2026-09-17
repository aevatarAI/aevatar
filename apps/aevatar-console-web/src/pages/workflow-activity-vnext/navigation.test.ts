import {
  buildWorkflowActivityEditorHref,
  buildWorkflowActivityNewHref,
  buildWorkflowActivitySettingsHref,
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

  it('builds an encoded canonical workflow editor URL', () => {
    expect(
      buildWorkflowActivityEditorHref('scope-alpha', 'workflow alpha'),
    ).toBe(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/workflow%20alpha',
    );
  });

  it.each([
    [
      'scope with space',
      'ai',
      '/scopes/scope%20with%20space/workflow-activity-vnext/settings',
    ],
    [
      'scope-alpha',
      'account',
      '/scopes/scope-alpha/workflow-activity-vnext/settings?section=account',
    ],
    [
      'scope-alpha',
      'advanced',
      '/scopes/scope-alpha/workflow-activity-vnext/settings?section=advanced',
    ],
  ] as const)('builds the canonical %s settings URL for %s', (scopeId, section, expected) => {
    expect(buildWorkflowActivitySettingsHref(scopeId, section)).toBe(expected);
  });
});
