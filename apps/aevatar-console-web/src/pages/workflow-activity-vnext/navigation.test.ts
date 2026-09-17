import {
  buildChannelBindHref,
  buildWorkflowActivityEditorHref,
  buildWorkflowActivityNewHref,
  buildWorkflowActivitySettingsHref,
  buildWorkflowActivityTemplatesHref,
} from './navigation';

describe('Workflow Activity vNext navigation', () => {
  it('keeps a NyxID bot identity in the canonical channel binding route', () => {
    expect(buildChannelBindHref('scope alpha', 'bot:one/two')).toBe(
      '/scopes/scope%20alpha/channels/bind/bot%3Aone%2Ftwo',
    );
  });

  it('builds an encoded canonical template creation URL', () => {
    expect(buildWorkflowActivityTemplatesHref('scope with space')).toBe(
      '/scopes/scope%20with%20space/workflows/new/templates',
    );
  });

  it('keeps the template change-method destination on the new workflow route', () => {
    expect(buildWorkflowActivityNewHref('scope-alpha')).toBe(
      '/scopes/scope-alpha/workflows/new',
    );
  });

  it('builds an encoded canonical workflow editor URL', () => {
    expect(
      buildWorkflowActivityEditorHref('scope-alpha', 'workflow alpha'),
    ).toBe('/scopes/scope-alpha/workflows/workflow%20alpha');
  });

  it.each([
    ['scope with space', 'ai', '/scopes/scope%20with%20space/settings'],
    ['scope-alpha', 'account', '/scopes/scope-alpha/settings?section=account'],
    [
      'scope-alpha',
      'advanced',
      '/scopes/scope-alpha/settings?section=advanced',
    ],
  ] as const)('builds the canonical %s settings URL for %s', (scopeId, section, expected) => {
    expect(buildWorkflowActivitySettingsHref(scopeId, section)).toBe(expected);
  });
});
