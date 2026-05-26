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
      screen.getByText('Studio 当前有部分能力暂时不可用'),
    ).toBeInTheDocument();
    expect(screen.getByText(/团队上下文：app context failed/)).toBeInTheDocument();
    expect(screen.getByText(/工作区设置：workspace failed/)).toBeInTheDocument();
    expect(screen.getByText(/登录状态：auth bootstrap warning/)).toBeInTheDocument();
    expect(screen.getByText('Studio workbench')).toBeInTheDocument();
  });
});
