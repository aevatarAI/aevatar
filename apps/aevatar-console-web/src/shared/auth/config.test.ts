import { getNyxIDRuntimeConfig } from './config';

describe('NyxID runtime config', () => {
  const originalEnv = { ...process.env };

  beforeEach(() => {
    process.env = {
      ...originalEnv,
    };
    delete process.env.NYXID_DEFAULT_SERVICE_SLUGS;
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
      defaultServiceSlugs: [],
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
      defaultServiceSlugs: [],
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
      defaultServiceSlugs: [],
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
      defaultServiceSlugs: [],
      configurationError:
        'NYXID_REDIRECT_URI must be a valid http(s) URL or a root-relative path such as /auth/callback.',
    });
  });

  it('normalizes configured default services and removes duplicates', () => {
    process.env.NYXID_DEFAULT_SERVICE_SLUGS =
      '" aevatar, ornn-api,chrono-llm-public,chrono-sandbox,api-lark-bot,ornn-api "';

    expect(getNyxIDRuntimeConfig().defaultServiceSlugs).toEqual([
      'aevatar',
      'ornn-api',
      'chrono-llm-public',
      'chrono-sandbox',
      'api-lark-bot',
    ]);
  });

  it('allows default service requests to be disabled explicitly', () => {
    process.env.NYXID_DEFAULT_SERVICE_SLUGS = '';

    expect(getNyxIDRuntimeConfig().defaultServiceSlugs).toEqual([]);
  });

  it('disables NyxID auth when a configured default service slug is invalid', () => {
    process.env.NYXID_DEFAULT_SERVICE_SLUGS = 'aevatar,Chrono Sandbox';

    expect(getNyxIDRuntimeConfig()).toEqual(
      expect.objectContaining({
        enabled: false,
        defaultServiceSlugs: [],
        configurationError:
          "NYXID_DEFAULT_SERVICE_SLUGS contains invalid service slug 'Chrono Sandbox'. Use comma-separated lowercase letters, numbers, and hyphens.",
      }),
    );
  });
});
