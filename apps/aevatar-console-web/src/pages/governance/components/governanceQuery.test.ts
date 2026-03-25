import {
  applyGovernanceServiceSelection,
  buildGovernanceServiceOptions,
  buildGovernanceHref,
  findGovernanceServiceOption,
  hasGovernanceScope,
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

  it('builds service picker options and applies their identity to the draft', () => {
    const options = buildGovernanceServiceOptions([
      {
        serviceKey: 't1/a1/n1/svc-1',
        tenantId: 't1',
        appId: 'a1',
        namespace: 'n1',
        serviceId: 'svc-1',
        displayName: 'Payments',
        defaultServingRevisionId: '',
        activeServingRevisionId: '',
        deploymentId: '',
        primaryActorId: '',
        deploymentStatus: '',
        endpoints: [],
        policyIds: [],
        updatedAt: '2026-03-24T00:00:00Z',
      },
    ]);

    expect(options).toEqual([
      {
        label: 'Payments (t1/a1/n1/svc-1)',
        value: 't1/a1/n1/svc-1',
        tenantId: 't1',
        appId: 'a1',
        namespace: 'n1',
        serviceId: 'svc-1',
      },
    ]);

    const selectedOption = findGovernanceServiceOption(options, {
      tenantId: '',
      appId: '',
      namespace: '',
      serviceId: 'svc-1',
      revisionId: '',
    });

    expect(selectedOption).toEqual(options[0]);
    expect(
      applyGovernanceServiceSelection(
        {
          tenantId: '',
          appId: '',
          namespace: '',
          serviceId: 'svc-1',
          revisionId: 'rev-2',
        },
        options[0],
      ),
    ).toEqual({
      tenantId: 't1',
      appId: 'a1',
      namespace: 'n1',
      serviceId: 'svc-1',
      revisionId: 'rev-2',
    });
  });

  it('requires a full scope before enabling service search', () => {
    expect(
      hasGovernanceScope({
        tenantId: 't1',
        appId: 'a1',
        namespace: 'n1',
        serviceId: '',
        revisionId: '',
      }),
    ).toBe(true);

    expect(
      hasGovernanceScope({
        tenantId: 't1',
        appId: '',
        namespace: 'n1',
        serviceId: '',
        revisionId: '',
      }),
    ).toBe(false);
  });
});
