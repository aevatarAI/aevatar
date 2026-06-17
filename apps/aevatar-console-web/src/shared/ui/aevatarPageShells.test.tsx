import { render, screen } from '@testing-library/react';
import { Grid } from 'antd';
import React from 'react';
import { AevatarContextDrawer } from './aevatarPageShells';

describe('AevatarContextDrawer', () => {
  afterEach(() => {
    jest.restoreAllMocks();
  });

  it('uses the requested bottom placement on narrow screens', () => {
    jest.spyOn(Grid, 'useBreakpoint').mockReturnValue({
      lg: false,
      md: false,
      sm: true,
      xl: false,
      xs: true,
      xxl: false,
    });

    render(
      <AevatarContextDrawer
        mobilePlacement="bottom"
        open
        title="Run diagnostics"
        onClose={jest.fn()}
      >
        <div>Drawer body</div>
      </AevatarContextDrawer>,
    );

    expect(screen.getByText('Run diagnostics')).toBeTruthy();
    expect(document.querySelector('.aevatar-context-drawer-bottom')).toBeTruthy();
    expect(
      document.querySelector('.aevatar-context-drawer-right'),
    ).toBeNull();
  });
});
