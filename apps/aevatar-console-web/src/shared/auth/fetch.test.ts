import { authFetch } from './fetch';
import { persistAuthSession } from './session';

describe('authFetch', () => {
  const originalFetch = global.fetch;

  beforeEach(() => {
    window.localStorage.clear();
    jest.spyOn(Date, 'now').mockReturnValue(1_700_000_000_000);
  });

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
    window.localStorage.clear();
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

  it('refreshes an expired NyxID session before sending the request', async () => {
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

    const fetchMock = jest.fn().mockImplementation(
      async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url.endsWith('/oauth/token')) {
          expect(init?.method).toBe('POST');
          return {
            ok: true,
            json: async () => ({
              access_token: 'new-access-token',
              refresh_token: 'new-refresh-token',
              token_type: 'Bearer',
              expires_in: 900,
              scope: 'openid profile email',
            }),
          } as Response;
        }

        if (url.endsWith('/oauth/userinfo')) {
          return {
            ok: true,
            json: async () => ({
              sub: 'user-1',
              email: 'after@example.com',
            }),
          } as Response;
        }

        return {
          ok: true,
        } as Response;
      },
    );
    global.fetch = fetchMock as typeof global.fetch;

    await authFetch('/api/agents');

    const [, init] = fetchMock.mock.calls[2] as [string, RequestInit | undefined];
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer new-access-token',
    );
  });
});
