import { describe, expect, it } from 'vitest';
import { buildScopeServiceQuery } from './api';

describe('scope service query helpers', () => {
  it('maps scope id onto the pinned scope-service identity defaults', () => {
    const query = buildScopeServiceQuery(' scope-a ', { take: '20' });
    const params = new URLSearchParams(query);

    expect(params.get('tenantId')).toBe('scope-a');
    expect(params.get('appId')).toBe('default');
    expect(params.get('namespace')).toBe('default');
    expect(params.get('take')).toBe('20');
  });

  it('keeps extra parameters when no scope fallback is available', () => {
    const query = buildScopeServiceQuery('', { take: '20' });
    const params = new URLSearchParams(query);

    expect(params.get('tenantId')).toBeNull();
    expect(params.get('appId')).toBeNull();
    expect(params.get('namespace')).toBeNull();
    expect(params.get('take')).toBe('20');
  });
});
