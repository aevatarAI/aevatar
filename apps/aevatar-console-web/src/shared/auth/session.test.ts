import { CONSOLE_HOME_ROUTE } from '@/shared/navigation/consoleHome';
import {
  buildAuthInitialState,
  loadRestorableAuthSession,
  loadStoredAuthSession,
  persistAuthSession,
  readStoredAuthSession,
  sanitizeReturnTo,
} from './session';

describe('auth session storage', () => {
  beforeEach(() => {
    window.localStorage.clear();
    jest.spyOn(Date, 'now').mockReturnValue(1_700_000_000_000);
  });

  afterEach(() => {
    jest.restoreAllMocks();
    window.localStorage.clear();
  });

  it('loads a persisted active auth session', () => {
    persistAuthSession({
      tokens: {
        accessToken: 'token-1',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
        email: 'user@example.com',
      },
    });

    expect(loadStoredAuthSession()).toEqual({
      tokens: {
        accessToken: 'token-1',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: 'user-1',
        email: 'user@example.com',
      },
    });
  });

  it('drops expired sessions while building initial auth state', () => {
    persistAuthSession({
      tokens: {
        accessToken: 'token-2',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() - 1,
      },
      user: {
        sub: 'user-2',
      },
    });

    const state = buildAuthInitialState({
      enabled: true,
      baseUrl: 'http://127.0.0.1:3001',
      clientId: 'client-1',
      redirectUri: 'http://127.0.0.1:5173/auth/callback',
      scope: 'openid profile email',
    });

    expect(state.isAuthenticated).toBe(false);
    expect(state.session).toBeUndefined();
    expect(window.localStorage.length).toBe(0);
  });

  it('keeps an expired session when a refresh token can restore it', () => {
    persistAuthSession({
      tokens: {
        accessToken: 'token-3',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() - 1,
        refreshToken: 'refresh-token-3',
      },
      user: {
        sub: 'user-3',
      },
    });

    expect(loadStoredAuthSession()).toBeNull();
    expect(loadRestorableAuthSession()).toEqual({
      tokens: {
        accessToken: 'token-3',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() - 1,
        refreshToken: 'refresh-token-3',
      },
      user: {
        sub: 'user-3',
      },
    });
    expect(readStoredAuthSession()).not.toBeNull();
  });

  it('accepts only safe in-app redirect targets', () => {
    expect(sanitizeReturnTo('/runs?tab=active')).toBe('/runtime/runs?tab=active');
    expect(sanitizeReturnTo('/gagents?scopeId=scope-a')).toBe('/runtime/gagents?scopeId=scope-a');
    expect(sanitizeReturnTo('https://example.com')).toBe(CONSOLE_HOME_ROUTE);
    expect(sanitizeReturnTo('/login?redirect=/overview')).toBe(CONSOLE_HOME_ROUTE);
    expect(sanitizeReturnTo('//evil.example.com')).toBe(CONSOLE_HOME_ROUTE);
  });
});
