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
      meta: 'rev-2 · Workspace script',
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

    render(
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

    expect(screen.getByLabelText('Team members')).toBeInTheDocument();
    expect(screen.getByText('Member inventory')).toBeInTheDocument();
    expect(screen.getByLabelText('Search team members')).toBeInTheDocument();
    expect(screen.getByLabelText('Create member')).toBeInTheDocument();
    expect(screen.getByText('Support Triage Router')).toBeInTheDocument();
    expect(screen.getByText('Studio content')).toBeInTheDocument();
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
    expect(
      within(screen.getByTestId('studio-lifecycle-stepper')).getByRole('button', {
        name: /^Build$/,
      }),
    ).toHaveAttribute('aria-current', 'step');
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

  it('keeps content visible when body scroll ownership is configured', () => {
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

    expect(screen.getByText('Studio content')).toBeInTheDocument();
  });

  it('can hide the member rail and lifecycle for focused launchpad empty states', () => {
    render(
      <StudioShell
        currentLifecycleStep="build"
        lifecycleSteps={lifecycleSteps}
        members={members}
        onSelectLifecycleStep={jest.fn()}
        onSelectMember={jest.fn()}
        pageTitle="Studio page"
        selectedMemberKey="workflow:workspace-demo"
        showLifecycle={false}
        showMemberRail={false}
      >
        <div>Script launchpad</div>
      </StudioShell>,
    );

    expect(screen.queryByLabelText('Team members')).not.toBeInTheDocument();
    expect(screen.queryByTestId('studio-lifecycle-section')).not.toBeInTheDocument();
    expect(screen.getByText('Script launchpad')).toBeInTheDocument();
  });

  it('keeps invoke content visible in page-scroll mode', () => {
    render(
      <StudioShell
        contentScrollMode="page"
        currentLifecycleStep="invoke"
        lifecycleSteps={lifecycleSteps}
        members={members}
        onSelectLifecycleStep={jest.fn()}
        onSelectMember={jest.fn()}
        pageTitle="Studio page"
        selectedMemberKey="workflow:workspace-demo"
      >
        <div>Invoke content</div>
      </StudioShell>,
    );

    expect(screen.getByText('Invoke content')).toBeInTheDocument();
    expect(screen.getByTestId('studio-shell-main')).toContainElement(
      screen.getByText('Invoke content'),
    );
  });

  it('renders observe content inside the shell content region', () => {
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

    expect(screen.getByTestId('studio-shell-content')).toContainElement(
      screen.getByText('Studio content'),
    );
  });
});
