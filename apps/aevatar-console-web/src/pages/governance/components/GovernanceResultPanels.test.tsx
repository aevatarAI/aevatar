import { render, screen } from '@testing-library/react';
import React from 'react';
import {
  formatGovernanceTimestamp,
  GovernanceSelectionNotice,
  GovernanceSummaryPanel,
} from './GovernanceResultPanels';

describe('GovernanceResultPanels', () => {
  it('renders the selected governance scope and summary metrics', () => {
    render(
      <GovernanceSummaryPanel
        title="Binding catalog"
        description="Inspect raw binding targets and attached policies."
        draft={{
          tenantId: 'tenant-a',
          namespace: 'ops',
          serviceId: 'svc-payments',
          revisionId: '',
        }}
        extraFields={[
          {
            label: 'Catalog updated',
            value: formatGovernanceTimestamp('2026-03-25T08:00:00Z'),
          },
        ]}
        metrics={[
          { label: 'Bindings', value: '8' },
          { label: 'With policies', value: '6', tone: 'success' },
        ]}
        status={{ color: 'processing', label: 'Loaded' }}
      />,
    );

    expect(screen.getByText('Binding catalog')).toBeInTheDocument();
    expect(screen.getByText('svc-payments')).toBeInTheDocument();
    expect(screen.getByText('tenant-a')).toBeInTheDocument();
    expect(screen.getByText('2026-03-25 08:00:00 UTC')).toBeInTheDocument();
    expect(screen.getByText('Bindings')).toBeInTheDocument();
    expect(screen.getByText('8')).toBeInTheDocument();
    expect(screen.getByText('Loaded')).toBeInTheDocument();
  });

  it('renders a lightweight selection notice', () => {
    render(
      <GovernanceSelectionNotice
        title="Select a platform service"
        description="Load a platform service identity before opening the raw governance catalog."
      />,
    );

    expect(screen.getByText('Select a platform service')).toBeInTheDocument();
    expect(
      screen.getByText(
        'Load a platform service identity before opening the raw governance catalog.',
      ),
    ).toBeInTheDocument();
  });
});
