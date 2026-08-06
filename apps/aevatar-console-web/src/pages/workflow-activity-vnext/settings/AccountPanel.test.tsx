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
  });

  it('reports service access review failures with a toast', async () => {
    mockLoginWithRedirect.mockRejectedValue(
      new Error('authorization endpoint unavailable'),
    );

    render(
      <AccountPanel
        identity={identity}
        onRefresh={jest.fn()}
        returnTo="/scopes/scope-alpha/workflow-activity-vnext/settings"
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
});
