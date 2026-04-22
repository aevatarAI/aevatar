import { fireEvent, render, screen, within } from '@testing-library/react';
import React from 'react';
import StudioShell, {
  type StudioLifecycleStep,
  type StudioShellMemberItem,
} from './StudioShell';

describe('StudioShell', () => {
  const members: readonly StudioShellMemberItem[] = [
    {
      key: 'workflow:workspace-demo',
      label: 'Support Triage Router',
      description: 'service-alpha',
      meta: 'Build focus · rev-1',
      canDelete: true,
      canRename: true,
      kind: 'workflow',
      tone: 'live',
    },
    {
      key: 'script:risk-review',
      label: 'risk-review',
      description: 'definition-1',
      meta: 'rev-2 · Scope script',
      kind: 'script',
      tone: 'draft',
    },
  ];

  const lifecycleSteps: readonly StudioLifecycleStep[] = [
    {
      key: 'build',
      label: 'Build',
      description: 'Edit the member implementation.',
      status: 'active',
    },
    {
      key: 'bind',
      label: 'Bind',
      description: 'Bring binding controls into Studio next.',
      status: 'planned',
      disabled: true,
    },
    {
      key: 'invoke',
      label: 'Invoke',
      description: 'Bring the invoke playground into Studio next.',
      status: 'planned',
      disabled: true,
    },
    {
      key: 'observe',
      label: 'Observe',
      description: 'Inspect run posture for the selected member.',
      status: 'available',
    },
  ];

  it('renders the member rail and forwards member and lifecycle selection', async () => {
    const handleCreateMember = jest.fn();
    const handleDeleteMember = jest.fn();
    const handleSelectMember = jest.fn();
    const handleSelectLifecycleStep = jest.fn();

    const { container } = render(
      <StudioShell
        currentLifecycleStep="build"
        inventoryActions={
          <div>
            <button
              aria-label="Create member"
              onClick={handleCreateMember}
              type="button"
            >
              Create member
            </button>
            <button
              aria-label="Delete Support Triage Router"
              onClick={() => handleDeleteMember('workflow:workspace-demo')}
              type="button"
            >
              Delete
            </button>
          </div>
        }
        lifecycleSteps={lifecycleSteps}
        members={members}
        onSelectLifecycleStep={handleSelectLifecycleStep}
        onSelectMember={handleSelectMember}
        pageTitle="Studio page"
        selectedMemberKey="workflow:workspace-demo"
      >
        <div>Studio content</div>
      </StudioShell>,
    );

    expect(container.firstChild).toHaveStyle({
      flex: '1',
      height: '100%',
      minHeight: '0',
      overflow: 'hidden',
      width: '100%',
    });
    expect(screen.getByLabelText('Team members')).toBeInTheDocument();
    expect(screen.getByText('Member inventory')).toBeInTheDocument();
    expect(screen.getByLabelText('Search team members')).toBeInTheDocument();
    expect(screen.getByLabelText('Create member')).toBeInTheDocument();
    expect(screen.getByText('Support Triage Router')).toBeInTheDocument();
    expect(screen.queryByText('Workspace panels')).toBeNull();
    expect(
      screen.queryByText(/Keep one member in focus while Build, Bind/i),
    ).toBeNull();
    expect(
      screen.queryByText('Inspect run posture for the selected member.'),
    ).toBeNull();
    expect(
      screen.getByRole('button', { name: /Observe/i }),
    ).not.toHaveAttribute('aria-current', 'step');
    expect(screen.getByTestId('studio-lifecycle-section')).toHaveStyle({
      gap: '6px',
      padding: '0 16px 10px',
    });
    expect(screen.getByTestId('studio-lifecycle-stepper')).toHaveStyle({
      display: 'flex',
      overflowX: 'auto',
    });
    expect(
      within(screen.getByTestId('studio-lifecycle-stepper')).getByRole('button', {
        name: /^Build$/,
      }),
    ).toHaveStyle({
      borderRadius: '999px',
      padding: '6px 14px',
    });
    expect(
      within(screen.getByTestId('studio-lifecycle-stepper')).getByRole('button', {
        name: /^Observe$/,
      }),
    ).toHaveAttribute('title', 'Inspect run posture for the selected member.');

    fireEvent.click(
      screen.getByRole('button', { name: 'Open team members help' }),
    );
    expect(
      await screen.findByText(/Keep one member in focus while Build, Bind/i),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /risk-review/i }));
    fireEvent.click(screen.getByRole('button', { name: /Observe/i }));
    fireEvent.click(screen.getByLabelText('Create member'));
    fireEvent.click(await screen.findByLabelText('Delete Support Triage Router'));

    expect(handleCreateMember).toHaveBeenCalled();
    expect(handleDeleteMember).toHaveBeenCalledWith('workflow:workspace-demo');
    expect(handleSelectMember).toHaveBeenCalledWith('script:risk-review');
    expect(handleSelectLifecycleStep).toHaveBeenCalledWith('observe');
  });

  it('keeps the content body scroll ownership configurable', () => {
    render(
      <StudioShell
        contentOverflow="hidden"
        currentLifecycleStep="build"
        lifecycleSteps={lifecycleSteps}
        members={members}
        onSelectLifecycleStep={jest.fn()}
        onSelectMember={jest.fn()}
        pageTitle="Studio page"
        selectedMemberKey="workflow:workspace-demo"
      >
        <div>Studio content</div>
      </StudioShell>,
    );

    expect(screen.getByText('Studio content').parentElement).toHaveStyle({
      display: 'flex',
      flex: '1',
      flexDirection: 'column',
      minHeight: '0',
      overflowX: 'hidden',
      overflowY: 'hidden',
      padding: '14px 16px 16px',
    });
  });

  it('keeps the shell content as a flex column so the studio editor can stretch', () => {
    render(
      <StudioShell
        currentLifecycleStep="observe"
        lifecycleSteps={lifecycleSteps}
        members={members}
        onSelectLifecycleStep={jest.fn()}
        onSelectMember={jest.fn()}
        pageTitle="Studio page"
        selectedMemberKey="workflow:workspace-demo"
      >
        <div>Studio content</div>
      </StudioShell>,
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
