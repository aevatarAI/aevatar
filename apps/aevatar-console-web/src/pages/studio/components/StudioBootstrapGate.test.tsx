import { render, screen } from '@testing-library/react';
import React from 'react';
import StudioBootstrapGate from './StudioBootstrapGate';

describe('StudioBootstrapGate', () => {
  it('renders lightweight bootstrap notices and keeps children mounted', () => {
    render(
      <StudioBootstrapGate
        appContextLoading
        appContextError={new Error('app context failed')}
        authLoading={false}
        authError={new Error('auth bootstrap warning')}
        workspaceLoading={false}
        workspaceError={new Error('workspace failed')}
      >
        <div>Studio workbench</div>
      </StudioBootstrapGate>,
    );

    expect(
      screen.getByText('Bootstrapping Studio host context'),
    ).toBeInTheDocument();
    expect(
      screen.getByText('Failed to load Studio app context'),
    ).toBeInTheDocument();
    expect(
      screen.getByText('Failed to load Studio workspace settings'),
    ).toBeInTheDocument();
    expect(
      screen.getByText('Studio authentication bootstrap returned an error'),
    ).toBeInTheDocument();
    expect(screen.getByText('Studio workbench')).toBeInTheDocument();
  });
});
