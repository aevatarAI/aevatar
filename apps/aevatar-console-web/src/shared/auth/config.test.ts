import { getNyxIDRuntimeConfig } from './config';

describe('NyxID runtime config', () => {
  const originalEnv = { ...process.env };

  beforeEach(() => {
    process.env = {
      ...originalEnv,
    };
    window.history.replaceState({}, '', '/login');
  });

  afterEach(() => {
    process.env = originalEnv;
  });

  it('normalizes the fallback callback redirect URI', () => {
    process.env.NYXID_REDIRECT_URI = '/auth/callback';

    expect(getNyxIDRuntimeConfig()).toEqual({
      enabled: true,
      baseUrl: '',
      clientId: '',
      redirectUri: `${window.location.origin}/auth/callback`,
      scope: '',
      configurationError: undefined,
    });
  });

  it('accepts an injected callback redirect URI wrapped in quotes', () => {
    process.env.NYXID_REDIRECT_URI = '"http://localhost:5173/auth/callback"';

    expect(getNyxIDRuntimeConfig()).toEqual({
      enabled: true,
      baseUrl: '',
      clientId: '',
      redirectUri: 'http://localhost:5173/auth/callback',
      scope: '',
      configurationError: undefined,
    });
  });

  it('does not bake a NyxID OAuth client into frontend runtime config', () => {
    process.env.NYXID_BASE_URL = 'undefined';
    process.env.NYXID_CLIENT_ID = 'undefined';
    process.env.NYXID_REDIRECT_URI = 'undefined';
    process.env.NYXID_SCOPE = 'undefined';

    expect(getNyxIDRuntimeConfig()).toEqual({
      enabled: true,
      baseUrl: '',
      clientId: '',
      redirectUri: `${window.location.origin}/auth/callback`,
      scope: '',
      configurationError: undefined,
    });
  });

  it('disables NyxID auth when the fallback callback redirect URI is invalid', () => {
    process.env.NYXID_REDIRECT_URI = '://bad-url';

    expect(getNyxIDRuntimeConfig()).toEqual({
      enabled: false,
      baseUrl: '',
      clientId: '',
      redirectUri: '',
      scope: '',
      configurationError:
        'NYXID_REDIRECT_URI must be a valid http(s) URL or a root-relative path such as /auth/callback.',
    });
  });
});
