import { screen } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import GovernanceIndexPage from './index';

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

describe('GovernanceIndexPage', () => {
  beforeEach(() => {
    window.history.replaceState(
      {},
      '',
      '/governance?tenantId=tenant-a&appId=app-a&namespace=default&serviceId=service-alpha&view=activation',
    );
  });

  it('renders the platform governance product framing', async () => {
    renderWithQueryClient(React.createElement(GovernanceIndexPage));

    expect(await screen.findByText('Aevatar / Platform')).toBeTruthy();
    expect(screen.getAllByText('Governance').length).toBeGreaterThan(0);
  });

  it('does not auto-select the first service when service context is missing', async () => {
    window.history.replaceState(
      {},
      '',
      '/governance?tenantId=tenant-a&appId=app-a&namespace=default',
    );

    renderWithQueryClient(React.createElement(GovernanceIndexPage));

    expect(await screen.findByText('选择一个服务')).toBeTruthy();
    expect(screen.getByText('当前范围 tenant-a / app-a / default')).toBeTruthy();
  });

  it('hides write actions when no service is selected', async () => {
    window.history.replaceState(
      {},
      '',
      '/governance?tenantId=tenant-a&appId=app-a&namespace=default&view=bindings',
    );

    renderWithQueryClient(React.createElement(GovernanceIndexPage));

    expect(await screen.findByText('选择一个服务')).toBeTruthy();
    expect(screen.queryByRole('button', { name: '新建绑定' })).toBeNull();
  });
});
