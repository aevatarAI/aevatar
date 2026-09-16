import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import * as React from 'react';
import { persistAuthSession } from '@/shared/auth/session';
import { history } from '@/shared/navigation/history';
import { studioApi } from '@/shared/studio/api';
import {
  createTestQueryClient,
  renderWithQueryClient,
} from '../../../tests/reactQueryTestUtils';
import { WORKFLOW_ACTIVITY_ACCOUNT_QUERY_KEY } from './account/useWorkflowActivityAccount';
import WorkflowHomePage from './WorkflowHomePage';

jest.mock('@/shared/studio/api', () => ({
  studioApi: { getAuthSession: jest.fn() },
}));

jest.mock('@/shared/navigation/history', () => ({
  history: { replace: jest.fn() },
}));

type StudioAuthSession = import('@/shared/studio/models').StudioAuthSession;

const getAuthSession = jest.mocked(studioApi.getAuthSession);

function account(scopeId: string | null): StudioAuthSession {
  return {
    enabled: true,
    authenticated: true,
    scopeId,
    scopeSource: 'claim:scope_id',
  };
}

function deferredAccount() {
  let resolve!: (value: StudioAuthSession) => void;
  const promise = new Promise<StudioAuthSession>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

describe('workflow home', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    getAuthSession.mockReset();
    setLocale('en-US', false);
    window.history.replaceState({}, '', '/workflows');
    persistAuthSession({
      tokens: {
        accessToken: 'test-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: { sub: 'user-not-a-scope' },
    });
  });

  it.each([
    'scope-alpha',
    'scope beta/secondary',
  ])('waits for the server scope %s before navigating', async (scopeId) => {
    const pending = deferredAccount();
    getAuthSession.mockReturnValue(pending.promise);
    renderWithQueryClient(<WorkflowHomePage />);

    expect(screen.getByRole('status')).toHaveTextContent(
      'Opening your workflows',
    );
    expect(history.replace).not.toHaveBeenCalled();
    await act(async () => pending.resolve(account(scopeId)));
    await waitFor(() =>
      expect(history.replace).toHaveBeenCalledWith(
        `/scopes/${encodeURIComponent(scopeId)}/workflows`,
      ),
    );
  });

  it('does not navigate from cached identity when refresh fails, and retries the real account read', async () => {
    const client = createTestQueryClient();
    client.setQueryData(
      WORKFLOW_ACTIVITY_ACCOUNT_QUERY_KEY,
      account('old-scope'),
    );
    getAuthSession.mockRejectedValueOnce(new Error('unavailable'));
    renderWithQueryClient(<WorkflowHomePage />, client);

    expect(
      await screen.findByText("We couldn't load your workspace. Try again."),
    ).toBeVisible();
    expect(history.replace).not.toHaveBeenCalled();
    getAuthSession.mockResolvedValueOnce(account('current-scope'));
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() =>
      expect(history.replace).toHaveBeenCalledWith(
        '/scopes/current-scope/workflows',
      ),
    );
  });

  it('keeps missing scope recoverable without substituting the browser user ID', async () => {
    getAuthSession.mockResolvedValue(account(null));
    renderWithQueryClient(<WorkflowHomePage />);

    expect(
      await screen.findByText(
        'Your account has no available workspace. Check your access and try again.',
      ),
    ).toBeVisible();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeEnabled();
    expect(history.replace).not.toHaveBeenCalled();
  });

  it('uses the configured Chinese locale for account-read recovery', async () => {
    setLocale('zh-CN', false);
    getAuthSession.mockRejectedValue(new Error('unavailable'));
    renderWithQueryClient(<WorkflowHomePage />);

    expect(
      await screen.findByText('暂时无法获取你的工作区，请重试。'),
    ).toBeVisible();
    expect(screen.getByRole('button', { name: /重\s*试/ })).toBeEnabled();
    expect(history.replace).not.toHaveBeenCalled();
  });

  it('uses the explicit server scope when server authentication is disabled', async () => {
    getAuthSession.mockResolvedValue({
      ...account('server-scope'),
      enabled: false,
      authenticated: false,
    });
    renderWithQueryClient(<WorkflowHomePage />);

    await waitFor(() =>
      expect(history.replace).toHaveBeenCalledWith(
        '/scopes/server-scope/workflows',
      ),
    );
  });

  it('sends an unauthenticated account through the existing login route', async () => {
    getAuthSession.mockResolvedValue({
      ...account('unusable-scope'),
      authenticated: false,
    });
    renderWithQueryClient(<WorkflowHomePage />);

    expect(
      await screen.findByText('Sign in to open your workflows.'),
    ).toBeVisible();
    expect(history.replace).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(history.replace).toHaveBeenCalledWith('/login');
    expect(history.replace).toHaveBeenCalledTimes(1);
  });
});
