import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import React from 'react';

function loadTooltip(): React.ComponentType<{
  children: React.ReactElement;
  placement?: 'topLeft';
  title: React.ReactNode;
}> {
  return require('./AevatarTooltip').default;
}

afterEach(() => cleanup());

describe('AevatarTooltip', () => {
  it('shows its help on hover', async () => {
    const AevatarTooltip = loadTooltip();
    render(
      <AevatarTooltip title="Configured workflow step count">
        <button type="button">Runs 3 steps</button>
      </AevatarTooltip>,
    );

    fireEvent.mouseEnter(screen.getByRole('button', { name: 'Runs 3 steps' }));
    expect(await screen.findByRole('tooltip')).toHaveTextContent(
      'Configured workflow step count',
    );
  });

  it('shows its help on keyboard focus and preserves placement overrides', async () => {
    const AevatarTooltip = loadTooltip();
    render(
      <AevatarTooltip placement="topLeft" title="Placement help">
        <button type="button">Inspect placement</button>
      </AevatarTooltip>,
    );

    fireEvent.focus(screen.getByRole('button', { name: 'Inspect placement' }));

    expect(
      (await screen.findByRole('tooltip')).closest('.ant-tooltip'),
    ).toHaveClass('ant-tooltip-placement-topLeft');
  });
});
