import { authFetch } from './fetch';
import { persistAuthSession } from './session';

describe('authFetch', () => {
  const originalFetch = global.fetch;
  const originalEnv = process.env;

  beforeEach(() => {
    window.localStorage.clear();
    jest.spyOn(Date, 'now').mockReturnValue(1_700_000_000_000);
    process.env = { ...originalEnv };
  });

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
    window.localStorage.clear();
    process.env = originalEnv;
  });

  it('injects a bearer token from the current NyxID session', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({ ok: true } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await authFetch('/api/agents');

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit | undefined];
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
  });

  it('preserves an explicit authorization header', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({ ok: true } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await authFetch('/api/agents', {
      headers: {
        Authorization: 'Bearer override-token',
      },
    });

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit | undefined];
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer override-token',
    );
  });

  it('does not exchange refresh tokens in the browser for expired sessions', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'expired-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() - 1,
        refreshToken: 'refresh-token',
      },
      user: {
        sub: 'user-1',
        email: 'before@example.com',
      },
    });

    const fetchMock = jest.fn().mockResolvedValue({ ok: true } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await authFetch('/api/agents');

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/agents');
    expect(new Headers(init?.headers).has('Authorization')).toBe(false);
    expect(
      fetchMock.mock.calls.some(([url]) => String(url).includes('/oauth/token')),
    ).toBe(false);
  });
});
