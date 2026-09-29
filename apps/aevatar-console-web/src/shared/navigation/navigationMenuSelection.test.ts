import { getNavigationSelectedKeys } from './navigationMenuSelection';

describe('getNavigationSelectedKeys', () => {
  it('does not select a primary navigation item for removed Team compatibility routes', () => {
    expect(getNavigationSelectedKeys('/teams/new')).toEqual([]);
  });

  it('does not select a legacy menu for unregistered team pages', () => {
    expect(getNavigationSelectedKeys('/scopes/scope-1/teams')).toEqual([]);
    expect(getNavigationSelectedKeys('/scopes/scope-1/teams/t-alpha')).toEqual(
      [],
    );
  });

  it('does not map removed legacy team detail pages back to My Teams', () => {
    expect(getNavigationSelectedKeys('/teams/scope-1')).toEqual([]);
  });

  it('does not select a legacy menu for unregistered governance pages', () => {
    expect(getNavigationSelectedKeys('/governance/bindings')).toEqual([]);
  });

  it('returns no selected key for hidden routes without a menu parent', () => {
    expect(getNavigationSelectedKeys('/studio')).toEqual([]);
  });

  it('leaves current console selection to its local navigation', () => {
    for (const pathname of [
      '/workflows',
      '/scopes/scope-1/workflows',
      '/scopes/scope-1/activity',
      '/scopes/scope-1/channels',
      '/scopes/scope-1/settings',
    ]) {
      expect(getNavigationSelectedKeys(pathname)).toEqual([]);
    }
  });
});
