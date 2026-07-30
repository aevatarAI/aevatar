import { getNyxIDRuntimeConfig } from './config';

describe('NyxID runtime config', () => {
  const originalEnv = { ...process.env };

  beforeEach(() => {
    process.env = {
      ...originalEnv,
      NYXID_BASE_URL: 'https://nyx.example/',
      NYXID_CLIENT_ID: 'console-client-1',
      NYXID_SCOPE:
        'openid profile email offline_access urn:nyxid:scope:broker_binding proxy',
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
      baseUrl: 'https://nyx.example',
      clientId: 'console-client-1',
      redirectUri: `${window.location.origin}/auth/callback`,
      scope:
        'openid profile email offline_access urn:nyxid:scope:broker_binding proxy',
      configurationError: undefined,
    });
  });

  it('accepts an injected callback redirect URI wrapped in quotes', () => {
    process.env.NYXID_REDIRECT_URI = '"http://localhost:5173/auth/callback"';

    expect(getNyxIDRuntimeConfig()).toEqual({
      enabled: true,
      baseUrl: 'https://nyx.example',
      clientId: 'console-client-1',
      redirectUri: 'http://localhost:5173/auth/callback',
      scope:
        'openid profile email offline_access urn:nyxid:scope:broker_binding proxy',
      configurationError: undefined,
    });
  });

  it('normalizes frontend OAuth values from the build environment', () => {
    process.env.NYXID_BASE_URL = '"https://login.nyx.example/"';
    process.env.NYXID_CLIENT_ID = '"console-client-2"';
    process.env.NYXID_REDIRECT_URI = 'undefined';
    process.env.NYXID_SCOPE = '"openid   profile  proxy"';

    expect(getNyxIDRuntimeConfig()).toEqual({
      enabled: true,
      baseUrl: 'https://login.nyx.example',
      clientId: 'console-client-2',
      redirectUri: `${window.location.origin}/auth/callback`,
      scope: 'openid profile proxy',
      configurationError: undefined,
    });
  });

  it('disables NyxID auth when the authority is missing', () => {
    process.env.NYXID_BASE_URL = 'undefined';

    expect(getNyxIDRuntimeConfig()).toEqual({
      enabled: false,
      baseUrl: '',
      clientId: 'console-client-1',
      redirectUri: `${window.location.origin}/auth/callback`,
      scope:
        'openid profile email offline_access urn:nyxid:scope:broker_binding proxy',
      configurationError:
        'NYXID_BASE_URL must be configured with the NyxID HTTP(S) authority.',
    });
  });

  it('disables NyxID auth when the authority is invalid', () => {
    process.env.NYXID_BASE_URL = '://bad-url';

    expect(getNyxIDRuntimeConfig()).toEqual({
      enabled: false,
      baseUrl: '',
      clientId: 'console-client-1',
      redirectUri: `${window.location.origin}/auth/callback`,
      scope:
        'openid profile email offline_access urn:nyxid:scope:broker_binding proxy',
      configurationError: 'NYXID_BASE_URL must be a valid HTTP(S) URL.',
    });
  });

  it('disables NyxID auth when the frontend OAuth client id is missing', () => {
    process.env.NYXID_CLIENT_ID = 'undefined';

    expect(getNyxIDRuntimeConfig()).toEqual({
      enabled: false,
      baseUrl: 'https://nyx.example',
      clientId: '',
      redirectUri: `${window.location.origin}/auth/callback`,
      scope:
        'openid profile email offline_access urn:nyxid:scope:broker_binding proxy',
      configurationError:
        'NYXID_CLIENT_ID must be configured with a non-empty public OAuth client id.',
    });
  });

  it('disables NyxID auth when the OAuth scope is missing', () => {
    process.env.NYXID_SCOPE = 'undefined';

    expect(getNyxIDRuntimeConfig()).toEqual({
      enabled: false,
      baseUrl: 'https://nyx.example',
      clientId: 'console-client-1',
      redirectUri: `${window.location.origin}/auth/callback`,
      scope: '',
      configurationError:
        'NYXID_SCOPE must be configured with at least one OAuth scope.',
    });
  });

  it('disables NyxID auth when the fallback callback redirect URI is invalid', () => {
    process.env.NYXID_REDIRECT_URI = '://bad-url';

    expect(getNyxIDRuntimeConfig()).toEqual({
      enabled: false,
      baseUrl: 'https://nyx.example',
      clientId: 'console-client-1',
      redirectUri: '',
      scope:
        'openid profile email offline_access urn:nyxid:scope:broker_binding proxy',
      configurationError:
        'NYXID_REDIRECT_URI must be a valid http(s) URL or a root-relative path such as /auth/callback.',
    });
  });
});
