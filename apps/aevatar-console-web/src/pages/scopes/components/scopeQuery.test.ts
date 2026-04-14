import {
  buildScopeHref,
  normalizeScopeDraft,
  readScopeQueryDraft,
} from './scopeQuery';

describe('scopeQuery', () => {
  it('reads scopeId from the query string', () => {
    expect(readScopeQueryDraft('?scopeId=scope-alpha')).toEqual({
      scopeId: 'scope-alpha',
    });
  });

  it('normalizes whitespace around the scopeId', () => {
    expect(
      normalizeScopeDraft({
        scopeId: ' scope-alpha ',
      }),
    ).toEqual({
      scopeId: 'scope-alpha',
    });
  });

  it('builds scope routes with preserved scope and selection context', () => {
    expect(
      buildScopeHref(
        '/scopes/assets',
        { scopeId: 'scope-alpha' },
        { tab: 'workflows', workflowId: 'wf-1' },
      ),
    ).toBe('/scopes/assets?scopeId=scope-alpha&tab=workflows&workflowId=wf-1');
  });
});
