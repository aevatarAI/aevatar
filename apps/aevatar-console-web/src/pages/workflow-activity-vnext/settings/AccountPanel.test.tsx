import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import AccountPanel from './AccountPanel';

const mockLoginWithRedirect = jest.fn();
const mockConsoleToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};

jest.mock('@/shared/auth/client', () => ({
  NyxIDAuthClient: jest.fn().mockImplementation(() => ({
    loginWithRedirect: mockLoginWithRedirect,
  })),
}));

jest.mock('@/shared/ui/ConsoleToast', () => ({
  useConsoleToast: () => mockConsoleToast,
}));

const identity = {
  displayName: { kind: 'value', value: 'Ada Lovelace' },
  email: { kind: 'value', value: 'ada@example.com' },
  emailVerified: true,
  expiry: { kind: 'value', value: 'Aug 7, 2026' },
  picture: null,
  provider: { kind: 'value', value: 'NyxID' },
  scope: { kind: 'value', value: 'scope-alpha' },
  sessionState: 'active',
  support: {
    groups: [],
    roles: [],
    subject: 'user-alpha',
  },
} as const;

describe('Workflow Activity vNext account panel', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockLoginWithRedirect.mockReset();
    mockLoginWithRedirect.mockResolvedValue(undefined);
  });

  it('starts service access review with the canonical Account settings URL', async () => {
    render(
      <AccountPanel
        accountSettingsHref="/scopes/scope-alpha/workflow-activity-vnext/settings?section=account"
        identity={identity}
      />,
    );

    fireEvent.click(
      screen.getByRole('button', { name: 'Manage service access' }),
    );

    await waitFor(() =>
      expect(mockLoginWithRedirect).toHaveBeenCalledWith({
        flow: 'serviceAccessReview',
        returnTo:
          '/scopes/scope-alpha/workflow-activity-vnext/settings?section=account',
      }),
    );
  });

  it('reports service access review failures with a toast', async () => {
    mockLoginWithRedirect.mockRejectedValue(
      new Error('authorization endpoint unavailable'),
    );

    render(
      <AccountPanel
        accountSettingsHref="/scopes/scope-alpha/workflow-activity-vnext/settings?section=account"
        identity={identity}
      />,
    );

    fireEvent.click(
      screen.getByRole('button', { name: 'Manage service access' }),
    );

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        'Could not start service access review. Try again.',
      ),
    );
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Manage service access' }),
    ).not.toHaveClass('ant-btn-loading');
  });

  it('keeps identity and access facts primary without placeholder product status', () => {
    render(
      <AccountPanel
        accountSettingsHref="/scopes/scope-alpha/workflow-activity-vnext/settings?section=account"
        identity={{
          ...identity,
          emailVerified: true,
          support: {
            groups: ['platform'],
            roles: ['operator'],
            subject: 'user-alpha',
          },
        }}
      />,
    );

    expect(screen.getByText('AL')).toBeInTheDocument();
    expect(screen.getByText('Ada Lovelace')).toBeInTheDocument();
    expect(screen.getByText('ada@example.com')).toBeInTheDocument();
    expect(screen.getByText('NyxID')).toBeInTheDocument();
    expect(screen.getByText('scope-alpha')).toBeInTheDocument();
    expect(screen.getByText('user-alpha')).toBeInTheDocument();
    expect(screen.getByText('operator')).toBeInTheDocument();
    expect(screen.getByText('platform')).toBeInTheDocument();
    expect(screen.getByText('Verified')).toBeInTheDocument();
    expect(screen.queryByText('Product access')).not.toBeInTheDocument();
    expect(screen.queryByText('Not loaded')).not.toBeInTheDocument();
    expect(
      screen.queryByText(
        'Capability details are not provided by the current account service.',
      ),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Refresh status' }),
    ).not.toBeInTheDocument();
  });

  it('shows provider user IDs when they use a machine identifier shape', () => {
    render(
      <AccountPanel
        accountSettingsHref="/scopes/scope-alpha/workflow-activity-vnext/settings?section=account"
        identity={{
          ...identity,
          support: {
            groups: [],
            roles: [],
            subject: 'ccb108c4-dcb3-473a-a0f7-e9859bb2f2a0',
          },
        }}
      />,
    );

    expect(screen.getByText('ccb108c4-dcb...9bb2f2a0')).toBeInTheDocument();
  });

  it('renders a compact signed-in state when optional profile fields are absent', () => {
    render(
      <AccountPanel
        accountSettingsHref="/scopes/scope-alpha/workflow-activity-vnext/settings?section=account"
        identity={{
          ...identity,
          displayName: { kind: 'not_provided' },
          email: { kind: 'not_provided' },
          emailVerified: null,
          expiry: { kind: 'not_provided' },
          scope: { kind: 'not_provided' },
          support: { groups: [], roles: [], subject: null },
        }}
      />,
    );

    expect(
      screen.getByRole('heading', { name: 'Signed in' }),
    ).toBeInTheDocument();
    expect(
      screen.getByText('Profile details are unavailable.'),
    ).toBeInTheDocument();
    expect(screen.queryByText('Not provided')).not.toBeInTheDocument();
    expect(screen.getByText('NyxID')).toBeInTheDocument();
  });
});
