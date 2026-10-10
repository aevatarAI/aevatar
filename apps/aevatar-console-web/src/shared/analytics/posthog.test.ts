import posthog from 'posthog-js';

type BeforeSendFn = import('posthog-js').BeforeSendFn;
type PostHogConfig = import('posthog-js').PostHogConfig;

jest.mock('posthog-js', () => ({
  __esModule: true,
  default: {
    init: jest.fn(),
    capture: jest.fn(),
    identify: jest.fn(),
    reset: jest.fn(),
    get_property: jest.fn(),
  },
}));

function loadAnalytics() {
  let analytics: typeof import('./posthog') | undefined;
  jest.isolateModules(() => {
    analytics = require('./posthog');
  });
  if (!analytics) throw new Error('Analytics module did not load');
  return analytics;
}

function initializedConfig(): Partial<PostHogConfig> {
  return jest.mocked(posthog.init).mock.calls[0][1] ?? {};
}

describe('console analytics boundary', () => {
  const originalEnv = process.env;

  beforeEach(() => {
    process.env = { ...originalEnv };
    delete process.env.AEVATAR_POSTHOG_KEY;
    delete process.env.AEVATAR_POSTHOG_HOST;
    window.history.replaceState({}, '', '/scopes/s-alpha/workflows');
    jest.clearAllMocks();
    jest.mocked(posthog.get_property).mockReturnValue(undefined);
  });

  afterEach(() => {
    process.env = originalEnv;
  });

  it('stays inert without project configuration and isolates SDK failures from runs', () => {
    const analytics = loadAnalytics();
    analytics.initializeConsoleAnalytics();
    analytics.captureConsoleEvent('workflow_started', {
      workflowId: 'wf-alpha',
      nodeCount: 2,
    });
    expect(posthog.init).not.toHaveBeenCalled();
    expect(posthog.capture).not.toHaveBeenCalled();

    process.env.AEVATAR_POSTHOG_KEY = 'phc-test';
    process.env.AEVATAR_POSTHOG_HOST = 'https://eu.i.posthog.com';
    jest.mocked(posthog.init).mockImplementationOnce(() => {
      throw new Error('Collector unavailable');
    });
    expect(analytics.initializeConsoleAnalytics).not.toThrow();
    analytics.initializeConsoleAnalytics();
    analytics.initializeConsoleAnalytics();
    expect(posthog.init).toHaveBeenCalledTimes(2);
    jest.mocked(posthog.capture).mockImplementationOnce(() => {
      throw new Error('Collector unavailable');
    });
    expect(() =>
      analytics.captureConsoleEvent('workflow_finished', {
        workflowId: 'wf-alpha',
        status: 'success',
        duration: 1234,
      }),
    ).not.toThrow();
  });

  it('captures vitals/errors and masks replay, OAuth URLs and exception credentials', () => {
    process.env.AEVATAR_POSTHOG_KEY = 'phc-test';
    process.env.AEVATAR_POSTHOG_HOST = 'https://eu.i.posthog.com';
    process.env.AEVATAR_POSTHOG_ENVIRONMENT = 'production';
    loadAnalytics().initializeConsoleAnalytics();
    const config = initializedConfig();
    expect(config).toMatchObject({
      api_host: 'https://eu.i.posthog.com',
      capture_pageview: 'history_change',
      capture_performance: {
        web_vitals: true,
        web_vitals_allowed_metrics: ['LCP', 'INP', 'CLS'],
      },
      capture_exceptions: true,
      disable_session_recording: false,
      session_recording: {
        maskAllInputs: true,
        maskTextSelector: '*',
        maskAllElementAttributes: false,
        recordHeaders: false,
        recordBody: false,
      },
    });
    const beforeSend = config.before_send as BeforeSendFn;
    const result = beforeSend({
      event: '$exception',
      uuid: 'event-alpha',
      timestamp: new Date(),
      $set: {
        $current_url: 'https://console.example.com/auth/callback?code=private',
        access_token: 'private-profile-token',
      },
      $set_once: {
        $referrer: 'https://identity.example.com/login?state=private',
      },
      properties: {
        $current_url: 'https://console.example.com/scopes?token=private#secret',
        password: 'private-password',
        $exception_list: [
          {
            value:
              'Failed at https://api.example.com/run?key=secret Bearer private-value',
          },
          {
            value:
              'GET /api/chat?access_token=private-relative failed: {"access_token":"private-json","refreshToken":"private-refresh"}; Authorization: Bearer private-header',
          },
        ],
      },
    });
    expect(result?.properties).toMatchObject({
      app: 'aevatar-console',
      environment: 'production',
      $current_url: 'https://console.example.com/scopes',
      password: '[redacted]',
      $exception_list: [
        { value: 'Failed at https://api.example.com/run Bearer [redacted]' },
        {
          value:
            'GET /api/chat failed: {"access_token":"[redacted]","refreshToken":"[redacted]"}; Authorization: [redacted]',
        },
      ],
    });
    expect(result?.$set).toEqual({
      $current_url: 'https://console.example.com/auth/callback',
      access_token: '[redacted]',
    });
    expect(result?.$set_once).toEqual({
      $referrer: 'https://identity.example.com/login',
    });
    const maskRequest = config.session_recording?.maskCapturedNetworkRequestFn;
    const request = {
      name: 'https://console.example.com/auth/callback?code=private',
      entryType: 'navigation',
      startTime: 0,
      duration: 1,
      requestHeaders: { Authorization: 'Bearer private' },
      responseBody: 'private',
      isInitial: true,
    };
    expect(maskRequest?.(request)).toMatchObject({
      name: 'https://console.example.com/auth/callback',
      requestHeaders: undefined,
      responseBody: undefined,
    });
    window.history.replaceState({}, '', '/auth/callback?code=private');
    expect(
      beforeSend({ event: '$pageview', uuid: 'event-beta', properties: {} }),
    ).toBeNull();
  });

  it('preserves replay layout while masking content and stylesheet resource credentials before compression', () => {
    process.env.AEVATAR_POSTHOG_KEY = 'phc-test';
    process.env.AEVATAR_POSTHOG_HOST = 'https://eu.i.posthog.com';
    loadAnalytics().initializeConsoleAnalytics();
    const maskAttribute =
      initializedConfig().session_recording?.maskAttributeFn;
    const section = document.createElement('section');

    expect(maskAttribute?.('class', 'styled-card', section)).toBe(
      'styled-card',
    );
    expect(maskAttribute?.('id', 'replay-card', section)).toBe('replay-card');
    expect(
      maskAttribute?.('style', 'width: 640px; min-height: 96px;', section),
    ).toBe('width: 640px; min-height: 96px;');
    for (const attribute of ['title', 'data-output', 'value', 'aria-label']) {
      expect(maskAttribute?.(attribute, 'private-output', section)).toBe(
        '**************',
      );
    }
    expect(
      maskAttribute?.(
        '_cssText',
        '@import "/styles.css?token=private#secret"; .styled-card { background: url(https://assets.example.com/card.png?token=private#secret); width: 640px; }',
        section,
      ),
    ).toBe(
      `@import "${window.location.origin}/styles.css"; .styled-card { background: url("https://assets.example.com/card.png"); width: 640px; }`,
    );
    expect(
      maskAttribute?.('href', '/theme.css?token=private#secret', section),
    ).toBe(`${window.location.origin}/theme.css`);
    expect(
      maskAttribute?.('src', 'data:text/plain,private-output', section),
    ).toBe('[redacted]');
    expect(maskAttribute?.('rr_width', '640px', section)).toBe('640px');
  });

  it('separates identified users across logout and account switches without sending profile fields', () => {
    process.env.AEVATAR_POSTHOG_KEY = 'phc-test';
    process.env.AEVATAR_POSTHOG_HOST = 'https://eu.i.posthog.com';
    const analytics = loadAnalytics();
    analytics.initializeConsoleAnalytics();
    // Persisted SDK identity survives full-page logout and a fresh app module.
    jest.mocked(posthog.get_property).mockReturnValue('previous-login');
    analytics.syncConsoleAnalyticsUser();
    jest.mocked(posthog.get_property).mockReturnValue(undefined);
    analytics.syncConsoleAnalyticsUser('user-alpha');
    jest.mocked(posthog.get_property).mockReturnValue('user-alpha');
    analytics.syncConsoleAnalyticsUser('user-alpha');
    analytics.syncConsoleAnalyticsUser('user-beta');
    jest.mocked(posthog.get_property).mockReturnValue('user-beta');
    analytics.syncConsoleAnalyticsUser();
    expect(posthog.identify).toHaveBeenNthCalledWith(1, 'user-alpha');
    expect(posthog.identify).toHaveBeenNthCalledWith(2, 'user-beta');
    expect(posthog.identify).toHaveBeenCalledTimes(2);
    expect(posthog.reset).toHaveBeenCalledTimes(3);
  });
});
