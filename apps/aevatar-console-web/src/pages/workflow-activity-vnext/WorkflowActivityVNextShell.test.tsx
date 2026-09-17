import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import * as React from 'react';
import { history } from '@/shared/navigation/history';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import WorkflowActivityVNextShell from './WorkflowActivityVNextShell';

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn().mockResolvedValue({
      enabled: true,
      authenticated: true,
      profile: {
        subject: 'user-alpha',
        name: 'Channel operator',
        email: 'operator@example.test',
        picture: null,
      },
      session: { authenticated: true, scopeId: 'scope-alpha' },
    }),
  },
}));

it('keeps resource and account-menu navigation in the current scope on desktop and mobile', async () => {
  const push = jest.spyOn(history, 'push').mockImplementation(() => {});
  renderWithQueryClient(
    <WorkflowActivityVNextShell
      scopeId="scope-alpha"
      activeSection="channels"
      title="Channels"
    >
      Channel content
    </WorkflowActivityVNextShell>,
  );
  const nav = screen.getByRole('navigation', { name: 'Workflow workbench' });
  for (const [name, path] of [
    ['Workflows', 'workflows'],
    ['Activity', 'activity'],
    ['Channels', 'channels'],
    ['Settings', 'settings'],
  ]) {
    expect(within(nav).getByRole('link', { name })).toHaveAttribute(
      'href',
      `/scopes/scope-alpha/${path}`,
    );
  }
  fireEvent.click(await screen.findByText('Channel operator'));
  fireEvent.click(await screen.findByRole('menuitem', { name: 'Settings' }));
  expect(push).toHaveBeenLastCalledWith('/scopes/scope-alpha/settings');
  fireEvent.click(screen.getByLabelText('Open navigation'));
  const drawer = await screen.findByRole('dialog');
  fireEvent.click(within(drawer).getByText('Channel operator'));
  fireEvent.click(await screen.findByRole('menuitem', { name: 'Settings' }));
  expect(push).toHaveBeenCalledTimes(2);
  expect(push).toHaveBeenLastCalledWith('/scopes/scope-alpha/settings');
});

it('sends account Settings through the owning page navigation guard', async () => {
  const push = jest.spyOn(history, 'push').mockImplementation(() => {});
  const guard = jest.fn();
  renderWithQueryClient(
    <WorkflowActivityVNextShell
      scopeId="scope-beta"
      activeSection="workflows"
      title="Workflow"
      onNavigate={guard}
    >
      Unsaved workflow
    </WorkflowActivityVNextShell>,
  );
  fireEvent.click(await screen.findByText('Channel operator'));
  fireEvent.click(await screen.findByRole('menuitem', { name: 'Settings' }));
  await waitFor(() =>
    expect(guard).toHaveBeenCalledWith('/scopes/scope-beta/settings'),
  );
  expect(push).not.toHaveBeenCalled();
  expect(screen.getByText('Unsaved workflow')).toBeInTheDocument();
});
