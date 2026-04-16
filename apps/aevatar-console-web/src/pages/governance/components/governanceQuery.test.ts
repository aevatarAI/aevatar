import {
  applyGovernanceServiceSelection,
  buildGovernanceServiceOptions,
  buildGovernanceWorkbenchHref,
  findGovernanceServiceOption,
  hasGovernanceScope,
  normalizeGovernanceDraft,
  normalizeGovernanceQuery,
  readGovernanceDraft,
  readGovernanceWorkbenchView,
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

  it('builds governance workbench routes that preserve service context', () => {
    expect(
      buildGovernanceWorkbenchHref({
        tenantId: 't1',
        appId: 'a1',
        namespace: 'n1',
        serviceId: 'svc-1',
        revisionId: 'rev-2',
      }, 'activation'),
    ).toBe(
      '/governance?tenantId=t1&appId=a1&namespace=n1&serviceId=svc-1&revisionId=rev-2&view=activation',
    );
  });

  it('defaults the workbench view to overview when the query is missing or invalid', () => {
    expect(readGovernanceWorkbenchView('')).toBe('overview');
    expect(readGovernanceWorkbenchView('?view=bindings')).toBe('bindings');
    expect(readGovernanceWorkbenchView('?view=audit')).toBe('changes');
    expect(readGovernanceWorkbenchView('?view=unknown')).toBe('overview');
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
        label: 'Payments (t1/n1/svc-1)',
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
        appId: '',
        namespace: 'n1',
        serviceId: '',
        revisionId: '',
      }),
    ).toBe(true);

    expect(
      hasGovernanceScope({
        tenantId: 't1',
        appId: '',
        namespace: '',
        serviceId: '',
        revisionId: '',
      }),
    ).toBe(false);
  });
});
