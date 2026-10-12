import posthog from 'posthog-js';

jest.mock('posthog-js', () => ({
  __esModule: true,
  default: { init: jest.fn(), capture: jest.fn() },
}));
jest.mock('web-vitals', () => ({ onTTFB: jest.fn() }));

type Transport = {
  apiFetch: typeof import('./fetch').apiFetch;
  authFetch: typeof import('../../auth/fetch').authFetch;
  requestJson: typeof import('./client').requestJson;
  persistAuthSession: typeof import('../../auth/session').persistAuthSession;
};

function loadTransport(enabled = true): Transport {
  if (!enabled) delete process.env.AEVATAR_POSTHOG_KEY;
  let transport: Transport | undefined;
  jest.isolateModules(() => {
    require('../../analytics/posthog').initializeConsoleAnalytics();
    transport = {
      apiFetch: require('./fetch').apiFetch,
      authFetch: require('../../auth/fetch').authFetch,
      requestJson: require('./client').requestJson,
      persistAuthSession: require('../../auth/session').persistAuthSession,
    };
  });
  if (!transport) throw new Error('Transport did not load');
  return transport;
}

function response(status: number, payload: unknown = {}): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: '',
    headers: new Headers({ 'Content-Type': 'application/json' }),
    text: jest.fn().mockResolvedValue(JSON.stringify(payload)),
    json: jest.fn().mockResolvedValue(payload),
  } as Response;
}

function deferredResponse() {
  let resolve!: (value: Response) => void;
  const promise = new Promise<Response>((settle) => {
    resolve = settle;
  });
  return { promise, resolve };
}

describe('API request telemetry integration', () => {
  const originalFetch = global.fetch;
  const originalEnv = process.env;
  const originalRequest = Object.getOwnPropertyDescriptor(
    globalThis,
    'Request',
  );
  let currentTime: number;
  let fetchMock: jest.Mock;

  beforeEach(() => {
    jest.clearAllMocks();
    jest.mocked(posthog.init).mockReset();
    jest.mocked(posthog.capture).mockReset();
    process.env = {
      ...originalEnv,
      AEVATAR_POSTHOG_KEY: 'phc-test',
      AEVATAR_POSTHOG_HOST: 'https://us.i.posthog.com',
      NYXID_BASE_URL: 'https://nyx.test',
      NYXID_CLIENT_ID: 'console-test-client',
      NYXID_SCOPE: 'openid',
      NYXID_REDIRECT_URI: '/auth/callback',
    };
    currentTime = 100;
    jest.spyOn(performance, 'now').mockImplementation(() => currentTime);
    fetchMock = jest.fn();
    global.fetch = fetchMock;
  });

  afterEach(() => {
    global.fetch = originalFetch;
    process.env = originalEnv;
    if (originalRequest) {
      Object.defineProperty(globalThis, 'Request', originalRequest);
    } else {
      Reflect.deleteProperty(globalThis, 'Request');
    }
  });

  it('measures authenticated requests without changing headers, body, signal or the returned response', async () => {
    const { authFetch, persistAuthSession } = loadTransport();
    persistAuthSession({
      tokens: {
        accessToken: 'private-access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: { sub: 'user-alpha' },
    });
    const result = response(201, { workflowId: 'wf-alpha' });
    fetchMock.mockImplementation(async () => {
      currentTime = 172;
      return result;
    });
    const signal = new AbortController().signal;
    const body = JSON.stringify({ name: 'private-workflow-name' });
    const input = '/api/workflows?access_token=private-query#private-fragment';

    expect(
      await authFetch(input, {
        method: 'post',
        body,
        signal,
        headers: { 'Content-Type': 'application/json' },
      }),
    ).toBe(result);
    const [sentInput, sentInit] = fetchMock.mock.calls[0];
    expect(sentInput).toBe(input);
    expect(sentInit).toMatchObject({ method: 'post', body });
    expect(sentInit.signal).toBe(signal);
    expect(sentInit.headers.get('Authorization')).toBe(
      'Bearer private-access-token',
    );
    expect(sentInit.headers.get('Content-Type')).toBe('application/json');
    expect(result.json).not.toHaveBeenCalled();
    expect(result.text).not.toHaveBeenCalled();
    expect(posthog.capture).toHaveBeenCalledTimes(1);
    expect(posthog.capture).toHaveBeenCalledWith('api_request', {
      url: `${window.location.origin}/api/workflows`,
      method: 'POST',
      status: 201,
      duration_ms: 72,
      success: true,
    });
  });

  it('reports HTTP failures while preserving the shared JSON client error mapping', async () => {
    const { requestJson } = loadTransport();
    const result = response(403, {
      code: 'SCOPE_ACCESS_DENIED',
      message: 'Forbidden',
    });
    fetchMock.mockImplementation(async () => {
      currentTime = 140;
      return result;
    });
    await expect(
      requestJson('/api/scopes/s-other/services', (value) => value),
    ).rejects.toMatchObject({
      name: 'HttpRequestError',
      status: 403,
      code: 'SCOPE_ACCESS_DENIED',
      message: 'Forbidden',
    });
    expect(result.text).toHaveBeenCalledTimes(1);
    expect(posthog.capture).toHaveBeenCalledTimes(1);
    expect(posthog.capture).toHaveBeenCalledWith('api_request', {
      url: `${window.location.origin}/api/scopes/s-other/services`,
      method: 'GET',
      status: 403,
      duration_ms: 40,
      success: false,
      error_type: 'http_error',
    });
  });

  it('keeps overlapping request timings independent and returns SSE before the stream ends', async () => {
    const { apiFetch } = loadTransport();
    const slow = deferredResponse();
    const fast = deferredResponse();
    const stream = new ReadableStream<Uint8Array>();
    const streamResult = { ...response(200), body: stream };
    const read = jest.spyOn(stream, 'getReader');
    fetchMock
      .mockReturnValueOnce(slow.promise)
      .mockReturnValueOnce(fast.promise);
    const slowRequest = apiFetch('/api/slow');
    currentTime = 120;
    const streamRequest = apiFetch(
      new URL('/api/events?token=private', window.location.origin),
    );
    currentTime = 150;
    fast.resolve(streamResult);
    expect(await streamRequest).toBe(streamResult);
    expect(read).not.toHaveBeenCalled();
    expect(stream.locked).toBe(false);
    currentTime = 260;
    slow.resolve(response(200));
    await slowRequest;
    expect(posthog.capture).toHaveBeenCalledTimes(2);
    expect(posthog.capture).toHaveBeenNthCalledWith(1, 'api_request', {
      url: `${window.location.origin}/api/events`,
      method: 'GET',
      status: 200,
      duration_ms: 30,
      success: true,
    });
    expect(posthog.capture).toHaveBeenNthCalledWith(2, 'api_request', {
      url: `${window.location.origin}/api/slow`,
      method: 'GET',
      status: 200,
      duration_ms: 160,
      success: true,
    });
    await stream.cancel();
  });

  it('reads Request metadata and method overrides without forwarding URL parameters to analytics', async () => {
    // jsdom lacks Request; only the browser-owned metadata is needed here.
    Object.defineProperty(globalThis, 'Request', {
      configurable: true,
      value: class {
        readonly method: string;
        constructor(
          readonly url: string,
          init?: RequestInit,
        ) {
          this.method = init?.method?.toUpperCase() ?? 'GET';
        }
      },
    });
    const { apiFetch } = loadTransport();
    const input = new Request(
      'https://api.example.com/items?token=private#secret',
      { method: 'post' },
    );
    const override = { method: 'patch' };
    fetchMock.mockResolvedValue(response(200));
    await apiFetch(input);
    await apiFetch(input, override);
    expect(fetchMock).toHaveBeenNthCalledWith(1, input);
    expect(fetchMock).toHaveBeenNthCalledWith(2, input, override);
    expect(posthog.capture).toHaveBeenNthCalledWith(1, 'api_request', {
      url: 'https://api.example.com/items',
      method: 'POST',
      status: 200,
      duration_ms: 0,
      success: true,
    });
    expect(posthog.capture).toHaveBeenNthCalledWith(2, 'api_request', {
      url: 'https://api.example.com/items',
      method: 'PATCH',
      status: 200,
      duration_ms: 0,
      success: true,
    });
  });

  it.each([
    {
      error: new TypeError('Failed to fetch private-url'),
      errorType: 'network_error',
      input: 'https://user:password@api.example.com/items?token=private#secret',
      telemetryUrl: 'https://api.example.com/items',
    },
    {
      error: new DOMException('Cancelled private-operation', 'AbortError'),
      errorType: 'aborted',
    },
    {
      error: new DOMException('Timed out private-operation', 'TimeoutError'),
      errorType: 'timeout',
    },
  ])('reports $errorType without replacing the original rejection', async ({
    error,
    errorType,
    input = '/api/items',
    telemetryUrl = `${window.location.origin}/api/items`,
  }) => {
    const { apiFetch } = loadTransport();
    fetchMock.mockRejectedValue(error);
    const request = apiFetch(input);
    currentTime = 155;
    await expect(request).rejects.toBe(error);
    expect(posthog.capture).toHaveBeenCalledTimes(1);
    expect(posthog.capture).toHaveBeenCalledWith('api_request', {
      url: telemetryUrl,
      method: 'GET',
      status: 0,
      duration_ms: 55,
      success: false,
      error_type: errorType,
    });
  });

  it('preserves responses and rejections when analytics is disabled or telemetry fails', async () => {
    const disabled = loadTransport(false);
    const result = response(200);
    fetchMock.mockResolvedValue(result);
    expect(await disabled.apiFetch('/api/items')).toBe(result);
    expect(posthog.capture).not.toHaveBeenCalled();

    process.env.AEVATAR_POSTHOG_KEY = 'phc-test';
    const enabled = loadTransport();
    jest.mocked(posthog.capture).mockImplementation(() => {
      throw new Error('Collector failed');
    });
    expect(await enabled.apiFetch('/api/items')).toBe(result);
    const error = new TypeError('Network unavailable');
    fetchMock.mockRejectedValueOnce(error);
    await expect(enabled.apiFetch('/api/items')).rejects.toBe(error);
    jest.mocked(performance.now).mockImplementation(() => {
      throw new Error('Clock unavailable');
    });
    expect(await enabled.apiFetch('/api/items')).toBe(result);
  });
});
