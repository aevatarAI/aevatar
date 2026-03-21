import { getNyxIDRuntimeConfig } from './config';

describe('NyxID runtime config', () => {
  const originalEnv = { ...process.env };

  beforeEach(() => {
    process.env = {
      ...originalEnv,
      NYXID_CLIENT_ID: 'client-1',
      NYXID_SCOPE: 'openid profile email',
    };
    window.history.replaceState({}, '', '/login');
  });

  afterEach(() => {
    process.env = originalEnv;
  });

  it('normalizes local hostnames without an explicit scheme', () => {
    process.env.NYXID_BASE_URL = 'localhost:3001';
    process.env.NYXID_REDIRECT_URI = '/auth/callback';

    expect(getNyxIDRuntimeConfig()).toEqual({
      enabled: true,
      baseUrl: 'http://localhost:3001',
      clientId: 'client-1',
      redirectUri: `${window.location.origin}/auth/callback`,
      scope: 'openid profile email',
      configurationError: undefined,
    });
  });

  it('accepts injected env values wrapped in quotes', () => {
    process.env.NYXID_BASE_URL = '"https://nyx.chrono-ai.fun"';
    process.env.NYXID_REDIRECT_URI = '"http://localhost:5173/auth/callback"';

    expect(getNyxIDRuntimeConfig()).toEqual({
      enabled: true,
      baseUrl: 'https://nyx.chrono-ai.fun',
      clientId: 'client-1',
      redirectUri: 'http://localhost:5173/auth/callback',
      scope: 'openid profile email',
      configurationError: undefined,
    });
  });

  it('falls back to defaults when env values are the string undefined', () => {
    process.env.NYXID_BASE_URL = 'undefined';
    process.env.NYXID_REDIRECT_URI = 'undefined';

    expect(getNyxIDRuntimeConfig()).toEqual({
      enabled: true,
      baseUrl: 'https://nyx.chrono-ai.fun',
      clientId: 'client-1',
      redirectUri: `${window.location.origin}/auth/callback`,
      scope: 'openid profile email',
      configurationError: undefined,
    });
  });

  it('disables NyxID auth when the base URL is invalid', () => {
    process.env.NYXID_BASE_URL = '://bad-url';
    process.env.NYXID_REDIRECT_URI = `${window.location.origin}/auth/callback`;

    expect(getNyxIDRuntimeConfig()).toEqual({
      enabled: false,
      baseUrl: '',
      clientId: 'client-1',
      redirectUri: `${window.location.origin}/auth/callback`,
      scope: 'openid profile email',
      configurationError:
        'NYXID_BASE_URL must be a valid http(s) URL or a root-relative path such as /nyxid.',
    });
  });
});
