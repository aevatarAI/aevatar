import {
  buildServiceDetailHref,
  buildServicesHref,
  readServiceIdFromPathname,
  readServiceQueryDraft,
  trimServiceQuery,
} from './serviceQuery';

describe('serviceQuery', () => {
  it('reads service catalog filters from the query string', () => {
    expect(
      readServiceQueryDraft('?tenantId=t1&appId=a1&namespace=n1&take=25'),
    ).toEqual({
      tenantId: 't1',
      appId: 'a1',
      namespace: 'n1',
      take: 25,
    });
  });

  it('trims catalog filter values before they become an API query', () => {
    expect(
      trimServiceQuery({
        tenantId: ' t1 ',
        appId: ' a1 ',
        namespace: ' n1 ',
        take: 200,
      }),
    ).toEqual({
      tenantId: 't1',
      appId: 'a1',
      namespace: 'n1',
      take: 200,
    });
  });

  it('builds list and detail URLs that preserve the service identity query', () => {
    const query = {
      tenantId: 't1',
      appId: 'a1',
      namespace: 'n1',
      take: 20,
    };

    expect(buildServicesHref(query)).toBe(
      '/services?tenantId=t1&appId=a1&namespace=n1&take=20',
    );
    expect(buildServiceDetailHref('service.alpha', query)).toBe(
      '/services/service.alpha?tenantId=t1&appId=a1&namespace=n1&take=20',
    );
  });

  it('reads the concrete service identifier from a detail route pathname', () => {
    expect(readServiceIdFromPathname('/services/service.alpha')).toBe(
      'service.alpha',
    );
  });
});
