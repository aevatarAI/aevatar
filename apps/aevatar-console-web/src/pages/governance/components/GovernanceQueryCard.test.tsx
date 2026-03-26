import { screen, waitFor } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import GovernanceQueryCard from './GovernanceQueryCard';

describe('GovernanceQueryCard', () => {
  it('hydrates missing identity fields from a unique selected service', async () => {
    const onChange = jest.fn();

    renderWithQueryClient(
      <GovernanceQueryCard
        draft={{
          tenantId: '',
          appId: '',
          namespace: '',
          serviceId: 'svc-1',
          revisionId: '',
        }}
        serviceOptions={[
          {
            label: 'Payments (t1/a1/n1/svc-1)',
            value: 't1/a1/n1/svc-1',
            tenantId: 't1',
            appId: 'a1',
            namespace: 'n1',
            serviceId: 'svc-1',
          },
        ]}
        onChange={onChange}
        onLoad={() => {}}
      />,
    );

    expect(
      screen.getByText(
        'Select a service to hydrate the platform tenantId, appId, and namespace for this raw governance view.',
      ),
    ).toBeInTheDocument();

    await waitFor(() =>
      expect(onChange).toHaveBeenCalledWith({
        tenantId: 't1',
        appId: 'a1',
        namespace: 'n1',
        serviceId: 'svc-1',
        revisionId: '',
      }),
    );
  });

  it('disables service search until scope is available', () => {
    renderWithQueryClient(
      <GovernanceQueryCard
        draft={{
          tenantId: '',
          appId: '',
          namespace: '',
          serviceId: '',
          revisionId: '',
        }}
        serviceOptions={[]}
        serviceSearchEnabled={false}
        onChange={() => {}}
        onLoad={() => {}}
      />,
    );

    expect(
      screen.getByText('Enter platform tenantId, appId, namespace first'),
    ).toBeInTheDocument();
    expect(screen.getByRole('combobox')).toBeDisabled();
    expect(
      screen.getByText(
        'This raw governance view needs platform tenantId, appId, and namespace first. Most user flows should stay on Scopes or open this page from Platform Services.',
      ),
    ).toBeInTheDocument();
  });
});
