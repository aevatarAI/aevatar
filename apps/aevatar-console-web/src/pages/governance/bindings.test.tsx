import { waitFor } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import GovernanceBindingsPage from './bindings';

jest.mock('@/shared/api/servicesApi', () => ({
  servicesApi: {
    getRevisions: jest.fn(async () => ({
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
      revisions: [],
    })),
    listServices: jest.fn(async () => [
      {
        serviceKey: 'tenant-a/app-a/default/service-alpha',
        serviceId: 'service-alpha',
        displayName: 'Service Alpha',
        tenantId: 'tenant-a',
        appId: 'app-a',
        namespace: 'default',
        endpoints: [],
        policyIds: ['policy-a'],
        activeServingRevisionId: 'rev-2',
        defaultServingRevisionId: 'rev-1',
        deploymentStatus: 'ready',
        deploymentId: 'deploy-1',
        primaryActorId: 'actor://service-alpha',
        updatedAt: '2026-03-25T10:00:00Z',
      },
    ]),
  },
}));

jest.mock('@/shared/api/governanceApi', () => ({
  governanceApi: {
    getBindings: jest.fn(async () => ({
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
      bindings: [
        {
          bindingId: 'binding-alpha',
          displayName: 'Binding Alpha',
          bindingKind: 'service',
          policyIds: ['policy-a'],
          retired: false,
          serviceRef: {
            identity: {
              tenantId: 'tenant-a',
              appId: 'app-a',
              namespace: 'default',
              serviceId: 'service-beta',
            },
            endpointId: 'chat',
          },
          connectorRef: null,
          secretRef: null,
        },
      ],
    })),
    getActivationCapability: jest.fn(async () => ({
      identity: {
        tenantId: 'tenant-a',
        appId: 'app-a',
        namespace: 'default',
        serviceId: 'service-alpha',
      },
      revisionId: '',
      missingPolicyIds: [],
      bindings: [],
      policies: [],
      endpoints: [],
    })),
    getEndpointCatalog: jest.fn(async () => ({
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
      endpoints: [],
    })),
    getPolicies: jest.fn(async () => ({
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
      policies: [],
    })),
  },
}));

describe('GovernanceBindingsPage', () => {
  beforeEach(() => {
    window.history.replaceState(
      {},
      '',
      '/governance/bindings?tenantId=tenant-a&appId=app-a&namespace=default&serviceId=service-alpha',
    );
  });

  it('redirects the bindings shortcut route into the unified governance workbench', async () => {
    renderWithQueryClient(React.createElement(GovernanceBindingsPage));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/governance');
    });
    expect(window.location.search).toContain('tenantId=tenant-a');
    expect(window.location.search).toContain('appId=app-a');
    expect(window.location.search).toContain('namespace=default');
    expect(window.location.search).toContain('serviceId=service-alpha');
    expect(window.location.search).toContain('view=bindings');
  });
});
