import { fireEvent, screen } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import ServiceDetailPage from './detail';

jest.mock('@/shared/api/servicesApi', () => ({
  servicesApi: {
    getService: jest.fn(async () => ({
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      serviceId: 'service-alpha',
      displayName: 'Service Alpha',
      endpoints: [{ endpointId: 'chat' }],
      policyIds: ['policy-a'],
      deploymentStatus: 'ready',
      updatedAt: '2026-03-25T10:00:00Z',
    })),
    getRevisions: jest.fn(async () => ({
      revisions: [
        {
          revisionId: 'rev-1',
          implementationKind: 'workflow',
          status: 'Published',
          artifactHash: 'abc123',
          publishedAt: '2026-03-25T10:00:00Z',
        },
      ],
    })),
    getDeployments: jest.fn(async () => ({
      deployments: [
        {
          deploymentId: 'deploy-1',
          revisionId: 'rev-1',
          status: 'Ready',
          primaryActorId: 'actor://service-alpha',
          activatedAt: '2026-03-25T10:00:00Z',
        },
      ],
    })),
    getServingSet: jest.fn(async () => ({
      targets: [
        {
          deploymentId: 'deploy-1',
          revisionId: 'rev-1',
          allocationWeight: 100,
          servingState: 'Ready',
          enabledEndpointIds: ['chat'],
        },
      ],
    })),
    getRollout: jest.fn(async () => null),
    getTraffic: jest.fn(async () => ({
      endpoints: [
        {
          endpointId: 'chat',
          targets: [
            {
              revisionId: 'rev-1',
              allocationWeight: 100,
              servingState: 'Ready',
            },
          ],
        },
      ],
    })),
  },
}));

describe('ServiceDetailPage', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/services/service-alpha');
  });

  it('renders summary fields and tabbed platform detail', async () => {
    renderWithQueryClient(React.createElement(ServiceDetailPage));

    fireEvent.click(screen.getByRole('button', { name: 'Reload service' }));

    expect(await screen.findByText('Summary')).toBeTruthy();
    expect(screen.getByText('Service key')).toBeTruthy();
    expect(screen.getByText('Deployment status')).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Endpoints (1)' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Traffic (1)' })).toBeTruthy();
  });
});
