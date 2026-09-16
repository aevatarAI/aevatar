import { render, waitFor } from '@testing-library/react';
import React from 'react';
import { ProtectedRouteRedirectGate } from './shared/auth/ProtectedRouteRedirectGate';

const mockedHistoryReplace = jest.fn();

jest.mock('./shared/navigation/history', () => ({
  history: {
    push: jest.fn(),
    replace: (...args: unknown[]) => mockedHistoryReplace(...args),
  },
}));

describe('ProtectedRouteRedirectGate', () => {
  beforeEach(() => {
    mockedHistoryReplace.mockReset();
    window.history.replaceState({}, '', '/scopes');
  });

  it('redirects protected routes into the login flow after mount', async () => {
    render(
      React.createElement(ProtectedRouteRedirectGate, {
        pathname: '/scopes',
      }),
    );

    await waitFor(() => {
      expect(mockedHistoryReplace).toHaveBeenCalledWith(
        '/login?redirect=%2Fscopes',
      );
    });
  });

  it('keeps Channel editing behind the login flow', async () => {
    window.history.replaceState(
      {},
      '',
      '/scopes/scope-alpha/channels/registration-alpha/edit',
    );

    render(
      React.createElement(ProtectedRouteRedirectGate, {
        pathname: '/scopes/scope-alpha/channels/registration-alpha/edit',
      }),
    );

    await waitFor(() => {
      expect(mockedHistoryReplace).toHaveBeenCalledWith(
        '/login?redirect=%2Fscopes%2Fscope-alpha%2Fchannels%2Fregistration-alpha%2Fedit',
      );
    });
  });
});
