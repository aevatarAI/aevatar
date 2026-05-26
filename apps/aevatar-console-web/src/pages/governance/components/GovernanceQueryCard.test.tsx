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
            label: 'Payments (t1/n1/svc-1)',
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
      screen.getByText('先填写团队、应用和命名空间'),
    ).toBeInTheDocument();
    expect(screen.getByRole('combobox')).toBeDisabled();
    expect(screen.getByRole('button', { name: '加载治理信息' })).toBeDisabled();
  });

  it('blocks loading until a service is selected', () => {
    renderWithQueryClient(
      <GovernanceQueryCard
        draft={{
          tenantId: 't1',
          appId: 'a1',
          namespace: 'n1',
          serviceId: '',
          revisionId: '',
        }}
        serviceOptions={[
          {
            label: 'Payments (t1/n1/svc-1)',
            value: 't1/a1/n1/svc-1',
            tenantId: 't1',
            appId: 'a1',
            namespace: 'n1',
            serviceId: 'svc-1',
          },
        ]}
        onChange={() => {}}
        onLoad={() => {}}
      />,
    );

    expect(screen.getByText('先选择服务')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '加载治理信息' })).toBeDisabled();
  });
});
