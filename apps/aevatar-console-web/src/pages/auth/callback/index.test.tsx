import { render, screen, waitFor } from '@testing-library/react';
import React from 'react';
import CallbackPage from './index';
import { NyxIDAuthClient } from '@/shared/auth/client';
import { CONSOLE_HOME_ROUTE } from '@/shared/navigation/consoleHome';
import { persistAuthSession } from '@/shared/auth/session';

const replaceLocation = jest.fn();
const handleRedirectCallback = jest.fn();

jest.mock('@/shared/auth/client', () => ({
  NyxIDAuthClient: jest.fn(),
}));

function mockLocationReplace(path = '/auth/callback?code=auth-code&state=state-1') {
  const url = new URL(path, 'http://localhost:8000');

  Object.defineProperty(window, 'location', {
    configurable: true,
    writable: true,
    value: {
      ...window.location,
      hash: url.hash,
      host: url.host,
      hostname: url.hostname,
      href: url.href,
      origin: url.origin,
      pathname: url.pathname,
      port: url.port,
      protocol: url.protocol,
      replace: replaceLocation,
      search: url.search,
      toString: () => url.href,
    },
  });
}

describe('NyxID callback page', () => {
  const originalLocationDescriptor = Object.getOwnPropertyDescriptor(
    window,
    'location',
  );

  beforeEach(() => {
    window.localStorage.clear();
    window.history.replaceState(
      {},
      '',
      '/auth/callback?code=auth-code&state=state-1',
    );
    replaceLocation.mockReset();
    handleRedirectCallback.mockReset();
    (NyxIDAuthClient as jest.Mock).mockImplementation(() => ({
      handleRedirectCallback,
    }));
    mockLocationReplace();
  });

  afterEach(() => {
    if (originalLocationDescriptor) {
      Object.defineProperty(window, 'location', originalLocationDescriptor);
    }
    jest.restoreAllMocks();
    window.localStorage.clear();
  });

  it('finalizes the OAuth callback even when a previous session exists', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'old-access-token',
        expiresAt: Date.now() + 60_000,
        expiresIn: 60,
        tokenType: 'Bearer',
      },
      user: {
        sub: 'old-user',
      },
    });
    handleRedirectCallback.mockResolvedValue({
      returnTo: '/runtime/runs',
      session: {
        tokens: {
          accessToken: 'new-access-token',
          expiresAt: Date.now() + 60_000,
          expiresIn: 60,
          tokenType: 'Bearer',
        },
        user: {
          sub: 'new-user',
        },
      },
    });

    render(React.createElement(CallbackPage));

    await waitFor(() => {
      expect(handleRedirectCallback).toHaveBeenCalledTimes(1);
    });
    expect(replaceLocation).toHaveBeenCalledWith('/runtime/runs');
  });

  it('skips callback finalization when no callback payload is present and a session exists', async () => {
    mockLocationReplace('/auth/callback');
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        expiresAt: Date.now() + 60_000,
        expiresIn: 60,
        tokenType: 'Bearer',
      },
      user: {
        sub: 'user-1',
      },
    });

    render(React.createElement(CallbackPage));

    await waitFor(() => {
      expect(replaceLocation).toHaveBeenCalledWith(CONSOLE_HOME_ROUTE);
    });
    expect(handleRedirectCallback).not.toHaveBeenCalled();
  });

  it('shows an actionable authorization error without exposing the machine code', async () => {
    handleRedirectCallback.mockRejectedValue(
      new Error(
        'Return to login and allow access to the Aevatar service in NyxID.',
      ),
    );

    render(React.createElement(CallbackPage));

    await waitFor(() => {
      expect(
        screen.getByText(
          'Return to login and allow access to the Aevatar service in NyxID.',
        ),
      ).toBeInTheDocument();
    });
    expect(
      screen.queryByText('required_service_access_missing'),
    ).not.toBeInTheDocument();
  });
});
