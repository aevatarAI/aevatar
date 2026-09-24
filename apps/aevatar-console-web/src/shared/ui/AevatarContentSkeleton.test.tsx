import { render, screen } from '@testing-library/react';
import React from 'react';
import AevatarContentSkeleton from './AevatarContentSkeleton';

describe('AevatarContentSkeleton', () => {
  it('renders configured table rows and columns as decorative geometry', () => {
    render(
      <AevatarContentSkeleton
        ariaLabel="Loading workflow catalog"
        columnWidths={[120, '2fr', '1fr']}
        rows={3}
        tableMinWidth={1100}
        variant="table"
      />,
    );

    expect(screen.getByRole('status')).toHaveAttribute('aria-busy', 'true');
    expect(screen.getByRole('status')).toHaveClass('aevatar-content-skeleton');
    expect(screen.getByRole('status')).toHaveAttribute('data-variant', 'table');
    expect(screen.getAllByTestId('aevatar-content-skeleton-row')).toHaveLength(
      3,
    );
    expect(screen.getAllByTestId('aevatar-content-skeleton-cell')).toHaveLength(
      9,
    );
    expect(screen.getByText('Loading workflow catalog')).toHaveClass(
      'aevatar-loading-visually-hidden',
    );
    expect(
      screen
        .getAllByTestId('aevatar-content-skeleton-cell')[0]
        .closest("[aria-hidden='true']"),
    ).toBeTruthy();
    expect(
      screen.getAllByTestId('aevatar-content-skeleton-row')[0],
    ).toHaveStyle('min-width: 1100px');
  });

  it('renders the configured list layout and item count', () => {
    render(
      <AevatarContentSkeleton
        ariaLabel="Loading connectors"
        listLayout="grid"
        rows={4}
        variant="list"
      />,
    );

    expect(screen.getByRole('status')).toHaveAttribute(
      'data-list-layout',
      'grid',
    );
    expect(
      screen.getAllByTestId('aevatar-content-skeleton-list-item'),
    ).toHaveLength(4);
  });

  it('renders canvas nodes and supports page-specific styling', () => {
    render(
      <AevatarContentSkeleton
        ariaLabel="Loading workflow runs"
        className="mission-wall-stage-skeleton"
        variant="canvas"
      />,
    );

    expect(screen.getByRole('status')).toHaveClass(
      'mission-wall-stage-skeleton',
    );
    expect(
      screen.getAllByTestId('aevatar-content-skeleton-node').length,
    ).toBeGreaterThan(1);
    expect(
      screen.getByTestId('aevatar-content-skeleton-connector'),
    ).toBeInTheDocument();
  });
});
