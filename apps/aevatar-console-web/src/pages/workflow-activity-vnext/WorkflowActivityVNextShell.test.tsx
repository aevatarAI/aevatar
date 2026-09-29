import { fireEvent, screen, within } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import * as React from 'react';
import { history } from '@/shared/navigation/history';
import { studioApi } from '@/shared/studio/api';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import WorkflowActivityVNextShell from './WorkflowActivityVNextShell';

jest.mock('@/shared/studio/api', () => ({
  studioApi: { getAuthSession: jest.fn() },
}));

jest.mock('@/shared/navigation/history', () => ({
  history: { push: jest.fn() },
}));

describe('workflow console navigation', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    setLocale('en-US', false);
    jest.mocked(studioApi.getAuthSession).mockResolvedValue({
      enabled: true,
      authenticated: true,
      scopeId: 'account-scope',
      subject: 'user-alpha',
      name: 'Example User',
    });
  });

  it('offers only the current console sections and opens settings in the route scope', async () => {
    renderWithQueryClient(
      <WorkflowActivityVNextShell
        activeSection="workflows"
        scopeId="route scope"
        title="Workflows"
      >
        <div>Workflow content</div>
      </WorkflowActivityVNextShell>,
    );
    const navigation = screen.getByRole('navigation', {
      name: 'Workflow workbench',
    });
    expect(
      within(navigation)
        .getAllByRole('link')
        .map((link) => link.getAttribute('href')),
    ).toEqual([
      '/scopes/route%20scope/workflows',
      '/scopes/route%20scope/activity',
      '/scopes/route%20scope/channels',
      '/scopes/route%20scope/settings',
    ]);
    fireEvent.click(await screen.findByText('Example User'));
    fireEvent.click(await screen.findByRole('menuitem', { name: /Settings/ }));
    expect(history.push).toHaveBeenCalledWith('/scopes/route%20scope/settings');
  });

  it.each([
    'desktop',
    'mobile',
  ])('keeps account settings behind the owning page navigation guard on %s', async (surface) => {
    const onNavigate = jest.fn();
    renderWithQueryClient(
      <WorkflowActivityVNextShell
        activeSection="workflows"
        scopeId="route-scope"
        title="Editor"
        onNavigate={onNavigate}
      >
        <div>Unsaved workflow</div>
      </WorkflowActivityVNextShell>,
    );
    let account = await screen.findByText('Example User');
    if (surface === 'mobile') {
      // jsdom does not apply mobile media queries; use the trigger's accessible label.
      fireEvent.click(screen.getByLabelText('Open navigation'));
      account = await within(await screen.findByRole('dialog')).findByText(
        'Example User',
      );
    }
    fireEvent.click(account);
    fireEvent.click(await screen.findByRole('menuitem', { name: /Settings/ }));
    expect(onNavigate).toHaveBeenCalledWith('/scopes/route-scope/settings');
    expect(history.push).not.toHaveBeenCalled();
    expect(screen.getByText('Unsaved workflow')).toBeInTheDocument();
  });
});
