import { render, screen } from '@testing-library/react';
import React from 'react';
import StudioBootstrapGate from './StudioBootstrapGate';

describe('StudioBootstrapGate', () => {
  it('renders a single bootstrap summary banner and keeps children mounted', () => {
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
      screen.getByText('Studio setup needs attention'),
    ).toBeInTheDocument();
    expect(screen.getByText(/App context: app context failed/)).toBeInTheDocument();
    expect(screen.getByText(/Workspace settings: workspace failed/)).toBeInTheDocument();
    expect(screen.getByText(/Authentication: auth bootstrap warning/)).toBeInTheDocument();
    expect(screen.getByText('Studio workbench')).toBeInTheDocument();
  });
});
