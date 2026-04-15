import { fireEvent, screen } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import ServicesPage from './index';

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
        endpoints: [{ endpointId: 'chat' }],
        policyIds: ['policy-a', 'policy-b'],
        activeServingRevisionId: 'rev-2',
        defaultServingRevisionId: 'rev-1',
        deploymentStatus: 'ready',
        deploymentId: 'deploy-1',
        primaryActorId: 'actor-1',
        updatedAt: '2026-03-25T10:00:00Z',
      },
    ]),
    getDeployments: jest.fn(async () => ({
      deployments: [
        {
          activatedAt: '2026-03-25T09:00:00Z',
          deploymentId: 'deploy-1',
          primaryActorId: 'actor-1',
          revisionId: 'rev-2',
          status: 'ready',
          updatedAt: '2026-03-25T10:00:00Z',
        },
      ],
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
    })),
    getRevisions: jest.fn(async () => ({
      revisions: [
        {
          artifactHash: 'hash-2',
          createdAt: '2026-03-25T08:00:00Z',
          endpoints: [],
          failureReason: '',
          implementationKind: 'workflow',
          preparedAt: '2026-03-25T08:05:00Z',
          publishedAt: '2026-03-25T08:10:00Z',
          retiredAt: null,
          revisionId: 'rev-2',
          status: 'Published',
        },
      ],
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
    })),
    getService: jest.fn(async () => ({
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      serviceId: 'service-alpha',
      displayName: 'Service Alpha',
      tenantId: 'tenant-a',
      appId: 'app-a',
      namespace: 'default',
      endpoints: [
        {
          description: '',
          displayName: 'Chat',
          endpointId: 'chat',
          kind: 'endpoint',
          requestTypeUrl: 'aevatar.services.ChatRequest',
          responseTypeUrl: 'aevatar.services.ChatReply',
        },
      ],
      policyIds: ['policy-a', 'policy-b'],
      activeServingRevisionId: 'rev-2',
      defaultServingRevisionId: 'rev-1',
      deploymentStatus: 'ready',
      deploymentId: 'deploy-1',
      primaryActorId: 'actor-1',
      updatedAt: '2026-03-25T10:00:00Z',
    })),
    getTraffic: jest.fn(async () => ({
      activeRolloutId: '',
      endpoints: [
        {
          endpointId: 'chat',
          targets: [
            {
              allocationWeight: 100,
              deploymentId: 'deploy-1',
              primaryActorId: 'actor-1',
              revisionId: 'rev-2',
              servingState: 'active',
            },
          ],
        },
      ],
      generation: 1,
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
    })),
  },
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn(async () => ({
      scope: {
        id: 'scope-1',
      },
    })),
  },
}));

describe('ServicesPage', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/services');
  });

  it('renders the unified services workbench with service data', async () => {
    renderWithQueryClient(React.createElement(ServicesPage));

    expect(await screen.findByText('Aevatar / Platform')).toBeTruthy();
    expect(screen.getAllByText('Services').length).toBeGreaterThan(0);
    expect(await screen.findByText('服务数')).toBeTruthy();
    expect(await screen.findByText('Service Alpha')).toBeTruthy();
    expect(screen.getByRole('button', { name: /详\s*情/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /治\s*理/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: '应用筛选' })).toBeTruthy();
    expect(screen.queryByText('Next Actions')).toBeNull();
    expect(screen.queryByText('No services matched the current scope.')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Inspect' })).toBeNull();
  });

  it('renders a compact service drawer instead of oversized metric cards', async () => {
    renderWithQueryClient(React.createElement(ServicesPage));

    fireEvent.click(await screen.findByRole('button', { name: /详\s*情/ }));

    expect(await screen.findByText('当前信息')).toBeTruthy();
    expect(screen.getByText('服务 Key')).toBeTruthy();
    expect(screen.getByText('tenant-a/app-a/default/service-alpha')).toBeTruthy();
    expect(screen.getAllByText('当前部署').length).toBeGreaterThan(0);
    expect(screen.getByText('最新版本')).toBeTruthy();
    expect(screen.getByText('入口数')).toBeTruthy();
    expect(screen.getByText('最大流量权重')).toBeTruthy();
    expect(screen.queryByText('Weight ceiling')).toBeNull();
  });
});
