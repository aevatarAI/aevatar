import { fireEvent, render, waitFor } from '@testing-library/react';
import React from 'react';
import { NyxIDAuthClient } from '@/shared/auth/client';
import { persistAuthSession } from '@/shared/auth/session';
import { CONSOLE_HOME_ROUTE } from '@/shared/navigation/consoleHome';
import { mockBrowserLocationNavigation } from '../../../../tests/browserLocationTestUtils';
import CallbackPage from './index';

let replaceLocation: jest.SpyInstance<void, [url: string | URL]>;
const handleRedirectCallback = jest.fn();
const loginWithRedirect = jest.fn();
const reviewReturnTo = '/scopes/scope-alpha/settings?section=account';

jest.mock('@/shared/auth/client', () => ({
  NyxIDAuthClient: jest.fn(),
}));

describe('NyxID callback page', () => {
  beforeEach(() => {
    window.localStorage.clear();
    window.history.replaceState(
      {},
      '',
      '/auth/callback?code=auth-code&state=state-1',
    );
    replaceLocation = mockBrowserLocationNavigation('replace');
    handleRedirectCallback.mockReset();
    loginWithRedirect.mockReset();
    loginWithRedirect.mockResolvedValue(undefined);
    (NyxIDAuthClient as jest.Mock).mockImplementation(() => ({
      handleRedirectCallback,
      loginWithRedirect,
    }));
  });

  afterEach(() => {
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
      returnTo: reviewReturnTo,
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
      expect(replaceLocation).toHaveBeenCalledWith(reviewReturnTo);
    });
  });

  it('shows retryable service access review cancellation without replacing the session route', async () => {
    const channelReturnTo =
      '/scopes/scope-alpha/channels/reg-alpha/edit?requiredServiceId=us-firecrawl';
    handleRedirectCallback.mockRejectedValue(
      Object.assign(new Error('OAuth error: access_denied'), {
        flow: 'serviceAccessReview',
        reason: 'oauthDenied',
        returnTo: channelReturnTo,
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
      returnTo: channelReturnTo,
    });
    expect(
      await findByRole('link', { name: 'Back to previous page' }),
    ).toHaveAttribute('href', channelReturnTo);
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
        returnTo: reviewReturnTo,
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
        returnTo: '/scopes/scope-1/workflows',
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
      returnTo: '/scopes/scope-1/workflows',
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
        returnTo: reviewReturnTo,
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

  it('sanitizes unsafe review error navigation before back or retry', async () => {
    handleRedirectCallback.mockRejectedValue(
      Object.assign(new Error('OAuth error: access_denied'), {
        flow: 'serviceAccessReview',
        reason: 'oauthDenied',
        returnTo: '//evil.example/account',
      }),
    );

    const { findByRole } = render(React.createElement(CallbackPage));
    const retryButton = await findByRole('button', {
      name: 'Retry service access review',
    });

    fireEvent.click(retryButton);

    expect(loginWithRedirect).toHaveBeenCalledWith({
      flow: 'serviceAccessReview',
      returnTo: CONSOLE_HOME_ROUTE,
    });
    expect(
      await findByRole('link', { name: 'Back to previous page' }),
    ).toHaveAttribute('href', CONSOLE_HOME_ROUTE);
  });

  it('skips callback finalization when no callback payload is present and a session exists', async () => {
    window.history.replaceState({}, '', '/auth/callback');
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
