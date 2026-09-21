import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { history } from '@/shared/navigation/history';
import { studioApi } from '@/shared/studio/api';
import {
  createTestQueryClient,
  renderWithQueryClient,
} from '../../../tests/reactQueryTestUtils';
import ConsoleScopeEntry from './index';

jest.mock('@/shared/studio/api', () => ({
  studioApi: { getAuthSession: jest.fn() },
}));
const getSession = jest.mocked(studioApi.getAuthSession);

beforeEach(() => {
  getSession.mockReset();
  jest.spyOn(history, 'replace').mockImplementation(() => {});
});

it.each([
  'scope-alice',
  'scope-bob',
])('opens the server-confirmed workspace %s', async (scopeId) => {
  getSession.mockResolvedValue({
    enabled: true,
    authenticated: true,
    subject: 'user-identity-is-not-scope',
    scopeId,
  });
  renderWithQueryClient(React.createElement(ConsoleScopeEntry));
  await waitFor(() =>
    expect(history.replace).toHaveBeenCalledWith(
      `/scopes/${scopeId}/workflows`,
    ),
  );
});

it('waits for current authentication instead of routing from an earlier account cache', async () => {
  const client = createTestQueryClient();
  client.setQueryData(['scopes', 'auth-session'], {
    authenticated: true,
    scopeId: 'scope-previous-account',
  });
  const currentSession = {
    enabled: true,
    authenticated: true,
    subject: 'user-current',
    scopeId: 'scope-current',
  };
  let resolve!: (session: typeof currentSession) => void;
  getSession.mockReturnValue(
    new Promise((done) => {
      resolve = done;
    }),
  );
  renderWithQueryClient(React.createElement(ConsoleScopeEntry), client);
  expect(history.replace).not.toHaveBeenCalled();
  await act(async () => resolve(currentSession));
  await waitFor(() => expect(history.replace).toHaveBeenCalledTimes(1));
  expect(history.replace).toHaveBeenCalledWith(
    '/scopes/scope-current/workflows',
  );
});

it.each([
  { authenticated: true, subject: 'user-without-scope', scopeId: ' ' },
  { authenticated: false, subject: 'user-expired', scopeId: 'scope-expired' },
])('does not invent a destination for $subject', async (session) => {
  getSession.mockResolvedValue({ enabled: true, ...session });
  renderWithQueryClient(React.createElement(ConsoleScopeEntry));
  await screen.findByText('Could not open your workspace');
  expect(history.replace).not.toHaveBeenCalled();
});

it('fails closed on session lookup failure and resolves the current scope on manual retry', async () => {
  getSession.mockRejectedValueOnce(new Error('TEST_ONLY_PRIVATE_DIAGNOSTIC'));
  renderWithQueryClient(React.createElement(ConsoleScopeEntry));
  await screen.findByText('Could not open your workspace');
  expect(history.replace).not.toHaveBeenCalled();
  expect(document.body).not.toHaveTextContent('TEST_ONLY_PRIVATE_DIAGNOSTIC');
  getSession.mockResolvedValue({
    enabled: true,
    authenticated: true,
    scopeId: 'scope-retried',
  });
  fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
  await waitFor(() =>
    expect(history.replace).toHaveBeenCalledWith(
      '/scopes/scope-retried/workflows',
    ),
  );
});
