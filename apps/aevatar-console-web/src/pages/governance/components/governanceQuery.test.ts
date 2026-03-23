import {
  buildGovernanceHref,
  normalizeGovernanceDraft,
  normalizeGovernanceQuery,
  readGovernanceDraft,
} from './governanceQuery';

describe('governanceQuery', () => {
  it('reads governance filters from the query string', () => {
    expect(
      readGovernanceDraft(
        '?tenantId=t1&appId=a1&namespace=n1&serviceId=svc-1&revisionId=rev-2',
      ),
    ).toEqual({
      tenantId: 't1',
      appId: 'a1',
      namespace: 'n1',
      serviceId: 'svc-1',
      revisionId: 'rev-2',
    });
  });

  it('normalizes service identity query and full governance draft separately', () => {
    const draft = {
      tenantId: ' t1 ',
      appId: ' a1 ',
      namespace: ' n1 ',
      serviceId: ' svc-1 ',
      revisionId: ' rev-2 ',
    };

    expect(normalizeGovernanceQuery(draft)).toEqual({
      tenantId: 't1',
      appId: 'a1',
      namespace: 'n1',
    });
    expect(normalizeGovernanceDraft(draft)).toEqual({
      tenantId: 't1',
      appId: 'a1',
      namespace: 'n1',
      serviceId: 'svc-1',
      revisionId: 'rev-2',
    });
  });

  it('builds governance routes that preserve service and revision context', () => {
    expect(
      buildGovernanceHref('/governance/activation', {
        tenantId: 't1',
        appId: 'a1',
        namespace: 'n1',
        serviceId: 'svc-1',
        revisionId: 'rev-2',
      }),
    ).toBe(
      '/governance/activation?tenantId=t1&appId=a1&namespace=n1&serviceId=svc-1&revisionId=rev-2',
    );
  });
});
