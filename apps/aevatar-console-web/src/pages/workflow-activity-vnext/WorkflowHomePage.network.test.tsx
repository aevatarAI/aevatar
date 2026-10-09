import { act, fireEvent, screen } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import * as React from 'react';
import {
  loadRestorableAuthSession,
  persistAuthSession,
} from '@/shared/auth/session';
import { history } from '@/shared/navigation/history';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import WorkflowHomePage from './WorkflowHomePage';

jest.mock('@/shared/navigation/history', () => ({
  history: { replace: jest.fn() },
}));

type StudioAuthSession = import('@/shared/studio/models').StudioAuthSession;

function account(scopeId: string): StudioAuthSession {
  return { enabled: true, authenticated: true, scopeId };
}

function response(scopeId: string): Response {
  return {
    ok: true,
    status: 200,
    json: async () => account(scopeId),
  } as Response;
}

describe('workflow home account network recovery', () => {
  const originalFetch = global.fetch;

  beforeEach(() => {
    jest.clearAllMocks();
    jest.useFakeTimers();
    setLocale('en-US', false);
    window.history.replaceState({}, '', '/workflows');
    persistAuthSession({
      tokens: {
        accessToken: 'test-access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: { sub: 'user-not-a-scope' },
    });
  });

  afterEach(() => {
    global.fetch = originalFetch;
    jest.useRealTimers();
  });

  it('recovers from a stalled account read and ignores its late scope after retry', async () => {
    let completeStalledRead!: (value: Response) => void;
    const stalledRead = new Promise<Response>((resolve) => {
      completeStalledRead = resolve;
    });
    const fetchMock = jest
      .fn<ReturnType<typeof fetch>, Parameters<typeof fetch>>()
      .mockReturnValueOnce(stalledRead);
    global.fetch = fetchMock;
    const { unmount } = renderWithQueryClient(<WorkflowHomePage />);

    await act(async () => jest.advanceTimersByTimeAsync(14_999));
    expect(screen.getByRole('status')).toHaveTextContent(
      'Opening your workflows',
    );
    expect(history.replace).not.toHaveBeenCalled();

    await act(async () => jest.advanceTimersByTimeAsync(1));
    expect(await screen.findByRole('button', { name: 'Retry' })).toBeEnabled();
    const [path, init] = fetchMock.mock.calls[0];
    expect(path).toBe('/api/auth/me');
    expect(init?.signal?.aborted).toBe(true);
    expect(loadRestorableAuthSession()?.user.sub).toBe('user-not-a-scope');

    fetchMock.mockResolvedValueOnce(response('current-scope'));
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await act(async () => jest.advanceTimersByTimeAsync(1));
    expect(history.replace).toHaveBeenCalledWith(
      '/scopes/current-scope/workflows',
    );

    await act(async () => completeStalledRead(response('stale-scope')));
    expect(history.replace).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    unmount();
  });

  it('offers retry when headers arrive but the account response body stalls', async () => {
    const body = new Promise<StudioAuthSession>(() => undefined);
    const fetchMock = jest
      .fn<ReturnType<typeof fetch>, Parameters<typeof fetch>>()
      .mockResolvedValue({
        ok: true,
        status: 200,
        json: () => body,
      } as Response);
    global.fetch = fetchMock;
    const { unmount } = renderWithQueryClient(<WorkflowHomePage />);

    await act(async () => jest.advanceTimersByTimeAsync(15_000));
    expect(await screen.findByRole('button', { name: 'Retry' })).toBeEnabled();
    expect(fetchMock.mock.calls[0][1]?.signal?.aborted).toBe(true);
    expect(history.replace).not.toHaveBeenCalled();
    unmount();
  });
});
