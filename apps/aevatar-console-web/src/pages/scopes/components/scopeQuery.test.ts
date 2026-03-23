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
        '/scopes/workflows',
        { scopeId: 'scope-alpha' },
        { workflowId: 'wf-1' },
      ),
    ).toBe('/scopes/workflows?scopeId=scope-alpha&workflowId=wf-1');
  });
});
