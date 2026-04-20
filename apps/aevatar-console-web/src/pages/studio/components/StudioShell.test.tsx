import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import StudioShell, { type StudioShellNavItem } from './StudioShell';

describe('StudioShell', () => {
  const navItems: readonly StudioShellNavItem[] = [
    {
      key: 'workflows',
      label: 'workflow',
      description: 'Browse workspace workflows and start new drafts.',
      count: 3,
    },
    {
      key: 'studio',
      label: '团队构建器',
      description: 'Edit the active draft and inspect execution runs.',
      count: 0,
    },
    {
      key: 'execution',
      label: '测试运行',
      description: 'Inspect active workflow runs.',
    },
    {
      key: 'roles',
      label: 'Agent 角色',
      description: 'Edit, import, and save workflow role definitions.',
    },
  ];

  it('renders the fixed icon rail and forwards navigation selection', () => {
    const handleSelectPage = jest.fn();

    const { container } = render(
      React.createElement(StudioShell, {
        currentPage: 'workflows',
        navItems,
        onSelectPage: handleSelectPage,
        pageTitle: 'Studio page',
        children: React.createElement('div', null, 'Studio content'),
      }),
    );

    expect(container.firstChild).toHaveStyle({
      flex: '1',
      height: '100%',
      minHeight: '0',
      overflow: 'hidden',
    });
    expect(screen.getByLabelText('Workbench')).toHaveStyle({ width: '56px' });
    expect(screen.getByLabelText('Workbench navigation')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'workflow' }),
    ).toHaveAttribute('aria-current', 'page');
    expect(screen.queryByText('Browse workspace workflows and start new drafts.')).toBeNull();
    expect(screen.queryByText('workflow')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: '团队构建器' }));
    fireEvent.click(screen.getByRole('button', { name: '编辑器设置' }));

    expect(handleSelectPage).toHaveBeenNthCalledWith(1, 'studio');
    expect(handleSelectPage).toHaveBeenNthCalledWith(2, 'settings');
  });

  it('keeps the content body scroll ownership configurable', () => {
    render(
      React.createElement(StudioShell, {
        contentOverflow: 'hidden',
        currentPage: 'workflows',
        navItems,
        onSelectPage: jest.fn(),
        pageTitle: 'Studio page',
        children: React.createElement('div', null, 'Studio content'),
      }),
    );

    expect(screen.getByText('Studio content').parentElement).toHaveStyle({
      display: 'flex',
      flex: '1',
      flexDirection: 'column',
      minHeight: '0',
      overflowX: 'hidden',
      overflowY: 'hidden',
      padding: '16px',
    });
  });

  it('keeps the shell content as a flex column so the studio editor can stretch', () => {
    render(
      React.createElement(StudioShell, {
        currentPage: 'studio',
        navItems,
        onSelectPage: jest.fn(),
        pageTitle: 'Studio page',
        children: React.createElement('div', null, 'Studio content'),
      }),
    );

    expect(screen.getByTestId('studio-shell-content')).toHaveStyle({
      display: 'flex',
      flex: '1',
      flexDirection: 'column',
      minHeight: '0',
      minWidth: '0',
      overflow: 'hidden',
    });
  });
});
