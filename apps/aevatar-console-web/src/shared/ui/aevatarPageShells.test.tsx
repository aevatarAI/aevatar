import { render, screen } from '@testing-library/react';
import { Grid } from 'antd';
import React from 'react';
import { AevatarContextDrawer, AevatarPageShell } from './aevatarPageShells';

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

describe('AevatarPageShell', () => {
  afterEach(() => {
    jest.restoreAllMocks();
  });

  it('removes nested inline page padding in viewport mode on compact screens', () => {
    jest.spyOn(Grid, 'useBreakpoint').mockReturnValue({
      lg: false,
      md: false,
      sm: false,
      xl: false,
      xs: true,
      xxl: false,
    });

    render(
      <AevatarPageShell title="Compact Team detail">
        <div>Team content</div>
      </AevatarPageShell>,
    );

    expect(
      screen.getByText('Team content').parentElement?.parentElement,
    ).toHaveStyle({ paddingInline: 0 });
  });
});
