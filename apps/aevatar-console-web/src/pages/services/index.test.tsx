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

  it('renders the catalog digest and related views with service data', async () => {
    renderWithQueryClient(React.createElement(ServicesPage));

    expect(await screen.findByText('Catalog digest')).toBeTruthy();
    expect(screen.getByText('Scope-first frontend')).toBeTruthy();
    expect(await screen.findByText('Service Alpha')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Open scopes' })).toBeTruthy();
    expect(
      screen.getByRole('button', { name: 'Open platform governance' }),
    ).toBeTruthy();
  });
});
