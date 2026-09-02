import { fireEvent, render, waitFor } from '@testing-library/react';
import React from 'react';
import { NyxIDAuthClient } from '@/shared/auth/client';
import { persistAuthSession } from '@/shared/auth/session';
import { CONSOLE_HOME_ROUTE } from '@/shared/navigation/consoleHome';
import CallbackPage from './index';

const replaceLocation = jest.fn();
const handleRedirectCallback = jest.fn();
const loginWithRedirect = jest.fn();

jest.mock('@/shared/auth/client', () => ({
  NyxIDAuthClient: jest.fn(),
  SERVICE_ACCESS_REVIEW_RETURN_TO: '/settings?section=account',
}));

function mockLocationReplace(
  path = '/auth/callback?code=auth-code&state=state-1',
) {
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
    loginWithRedirect.mockReset();
    loginWithRedirect.mockResolvedValue(undefined);
    (NyxIDAuthClient as jest.Mock).mockImplementation(() => ({
      handleRedirectCallback,
      loginWithRedirect,
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

  it('returns to Account settings after service access review succeeds', async () => {
    handleRedirectCallback.mockResolvedValue({
      flow: 'serviceAccessReview',
      returnTo: '/settings?section=account',
      session: {
        tokens: {
          accessToken: 'review-access-token',
          expiresAt: Date.now() + 60_000,
          expiresIn: 60,
          tokenType: 'Bearer',
        },
        user: {
          sub: 'user-1',
        },
      },
    });

    render(React.createElement(CallbackPage));

    await waitFor(() => {
      expect(replaceLocation).toHaveBeenCalledWith('/settings?section=account');
    });
  });

  it('shows retryable service access review cancellation without replacing the session route', async () => {
    handleRedirectCallback.mockRejectedValue(
      Object.assign(new Error('OAuth error: access_denied'), {
        flow: 'serviceAccessReview',
        reason: 'oauthDenied',
        returnTo: '/settings?section=account',
      }),
    );

    const { findByRole, findByText } = render(
      React.createElement(CallbackPage),
    );

    expect(
      await findByText(
        'NyxID service access review was cancelled or denied. Your current Studio session is still active.',
      ),
    ).toBeTruthy();
    const retryButton = await findByRole('button', {
      name: 'Retry service access review',
    });
    fireEvent.click(retryButton);
    expect(loginWithRedirect).toHaveBeenCalledWith({
      flow: 'serviceAccessReview',
      returnTo: '/settings?section=account',
    });
    expect(
      await findByRole('link', { name: 'Back to Account settings' }),
    ).toHaveAttribute('href', '/settings?section=account');
    expect(replaceLocation).not.toHaveBeenCalled();
  });

  it.each([
    [
      'requiredServiceAccessMissing',
      'Required service access is still missing. Return to NyxID and keep every service marked as required by Aevatar selected.',
    ],
    [
      'issuedBindingInvalid',
      'The new NyxID authorization expired before Aevatar could use it. Start the review again.',
    ],
    [
      'issuedBindingProbeFailed',
      'NyxID could not verify the new service authorization. Try again in a moment.',
    ],
    [
      'bindingProbeFailed',
      'NyxID could not verify the current service authorization. Try again in a moment.',
    ],
  ])('shows localized review guidance for %s', async (reason, message) => {
    handleRedirectCallback.mockRejectedValue(
      Object.assign(new Error('raw'), {
        flow: 'serviceAccessReview',
        reason,
        returnTo: '/settings?section=account',
      }),
    );
    const { findByRole } = render(React.createElement(CallbackPage));
    expect(await findByRole('alert')).toHaveTextContent(message);
  });

  it('requests consent when sign-in is missing required service access', async () => {
    handleRedirectCallback.mockRejectedValue(
      Object.assign(new Error('required_service_access_missing'), {
        flow: 'signIn',
        reason: 'requiredServiceAccessMissing',
        returnTo: '/scopes/scope-1/workflow-activity-vnext/workflows',
      }),
    );

    const { findByRole } = render(React.createElement(CallbackPage));
    const retryButton = await findByRole('button', {
      name: 'Try sign-in again',
    });

    fireEvent.click(retryButton);

    expect(loginWithRedirect).toHaveBeenCalledWith({
      flow: 'signIn',
      prompt: 'consent',
      returnTo: '/scopes/scope-1/workflow-activity-vnext/workflows',
    });
  });

  it('preserves auth finalization network details for callback failures', async () => {
    handleRedirectCallback.mockRejectedValue(
      Object.assign(
        new Error(
          'Error occurred while trying to proxy: localhost:5174/api/auth/nyxid/finalize to https://aevatar-console-backend-api.aevatar.ai/ [ECONNRESET]',
        ),
        {
          flow: 'signIn',
          reason: 'signInFailed',
          returnTo: '/login',
        },
      ),
    );

    const { findByRole } = render(React.createElement(CallbackPage));

    const alert = await findByRole('alert');
    expect(alert).toHaveTextContent('/api/auth/nyxid/finalize');
    expect(alert).toHaveTextContent('ECONNRESET');
    expect(alert).not.toHaveTextContent(
      'The login status is temporarily unavailable, please refresh and try again.',
    );
  });

  it('keeps service access review retryable when authorization restart fails', async () => {
    handleRedirectCallback.mockRejectedValue(
      Object.assign(new Error('raw'), {
        flow: 'serviceAccessReview',
        reason: 'serviceAccessReviewUnavailable',
        returnTo: '/settings?section=account',
      }),
    );
    loginWithRedirect.mockRejectedValueOnce(new Error('config unavailable'));
    const { findByRole } = render(React.createElement(CallbackPage));
    const retryButton = await findByRole('button', {
      name: 'Retry service access review',
    });
    fireEvent.click(retryButton);
    expect(await findByRole('alert')).toHaveTextContent(
      'Could not restart service access review. Try again.',
    );
    await waitFor(() => expect(retryButton).not.toHaveClass('ant-btn-loading'));
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
});
