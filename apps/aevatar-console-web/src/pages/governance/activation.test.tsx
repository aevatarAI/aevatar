import { waitFor } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import GovernanceActivationPage from './activation';

jest.mock('@/shared/api/servicesApi', () => ({
  servicesApi: {
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
    getRevisions: jest.fn(async () => ({
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
      revisions: [
        {
          revisionId: 'rev-2',
          implementationKind: 'workflow',
          status: 'Published',
          artifactHash: 'hash-2',
          failureReason: '',
          endpoints: [],
          createdAt: '2026-03-25T08:00:00Z',
          preparedAt: '2026-03-25T08:05:00Z',
          publishedAt: '2026-03-25T08:10:00Z',
          retiredAt: null,
        },
      ],
    })),
  },
}));

jest.mock('@/shared/api/governanceApi', () => ({
  governanceApi: {
    getBindings: jest.fn(async () => ({
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
      bindings: [],
    })),
    getActivationCapability: jest.fn(async () => ({
      identity: {
        tenantId: 'tenant-a',
        appId: 'app-a',
        namespace: 'default',
        serviceId: 'service-alpha',
      },
      revisionId: 'rev-2',
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

describe('GovernanceActivationPage', () => {
  beforeEach(() => {
    window.history.replaceState(
      {},
      '',
      '/governance/activation?tenantId=tenant-a&appId=app-a&namespace=default&serviceId=service-alpha',
    );
  });

  it('redirects the activation shortcut route into the unified governance workbench', async () => {
    renderWithQueryClient(React.createElement(GovernanceActivationPage));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/governance');
    });
    expect(window.location.search).toContain('tenantId=tenant-a');
    expect(window.location.search).toContain('appId=app-a');
    expect(window.location.search).toContain('namespace=default');
    expect(window.location.search).toContain('serviceId=service-alpha');
    expect(window.location.search).toContain('view=activation');
  });
});
