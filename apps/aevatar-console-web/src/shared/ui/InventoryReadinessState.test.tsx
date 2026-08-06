import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import InventoryReadinessState from './InventoryReadinessState';

describe('InventoryReadinessState', () => {
  it('renders a table skeleton without presenting loading copy or an empty inventory', () => {
    render(
      <InventoryReadinessState
        description="Keep the current inventory visible until the request resolves."
        kind="loading"
        title="Loading inventory"
      />,
    );

    expect(screen.getByRole('status')).toHaveAttribute('aria-busy', 'true');
    expect(screen.getByRole('status')).toHaveAttribute('data-variant', 'table');
    expect(screen.getAllByTestId('aevatar-content-skeleton-row')).toHaveLength(
      4,
    );
    expect(screen.getAllByTestId('aevatar-content-skeleton-cell')).toHaveLength(
      32,
    );
    expect(
      screen.getAllByTestId('aevatar-content-skeleton-row')[0],
    ).toHaveStyle('min-width: 1100px');
    expect(screen.getByText('Loading inventory')).toHaveClass(
      'aevatar-loading-visually-hidden',
    );
    expect(
      screen.queryByText(
        'Keep the current inventory visible until the request resolves.',
      ),
    ).toBeNull();
    expect(screen.queryByText('No inventory')).toBeNull();
  });

  it('renders error action without falling through to empty state', () => {
    const retry = jest.fn();

    render(
      <InventoryReadinessState
        action={{ label: 'Retry inventory', onClick: retry }}
        description="The inventory query failed."
        kind="error"
        title="Inventory unavailable"
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Retry inventory' }));

    expect(retry).toHaveBeenCalledTimes(1);
    expect(screen.getByText('Inventory unavailable')).toBeTruthy();
    expect(screen.queryByText('No inventory')).toBeNull();
  });

  it('renders empty state with an operator action', () => {
    const refine = jest.fn();

    render(
      <InventoryReadinessState
        action={{ label: 'Refine scope', onClick: refine }}
        description="Try a narrower Team, App, or Namespace."
        kind="empty"
        title="No inventory"
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Refine scope' }));

    expect(refine).toHaveBeenCalledTimes(1);
    expect(
      screen.getByText('Try a narrower Team, App, or Namespace.'),
    ).toBeTruthy();
  });
});
