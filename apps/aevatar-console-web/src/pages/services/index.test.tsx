import { screen } from '@testing-library/react';
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
        updatedAt: '2026-03-25T10:00:00Z',
      },
    ]),
  },
}));

describe('ServicesPage', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/services');
  });

  it('renders the unified services workbench with service data', async () => {
    renderWithQueryClient(React.createElement(ServicesPage));

    expect(await screen.findByText('Control Digest')).toBeTruthy();
    expect(screen.getByText('Service Workbench')).toBeTruthy();
    expect(await screen.findByText('Service Alpha')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Open Deployments' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Open Governance' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Inspect' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Governance' })).toBeTruthy();
  });
});
