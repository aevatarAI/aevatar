import { fireEvent, screen } from '@testing-library/react';
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

  it('hides the raw picker when a service context is already selected', async () => {
    renderWithQueryClient(React.createElement(GovernanceBindingsPage));

    expect(await screen.findByText('Current service context')).toBeTruthy();
    expect(screen.getAllByText('Bindings').length).toBeGreaterThan(0);
    expect(screen.queryByRole('button', { name: 'Load governance' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Change service' }));

    expect(await screen.findByRole('button', { name: 'Load governance' })).toBeTruthy();
  });
});
