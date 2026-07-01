import { scriptsApi } from './scriptsApi';
import { persistAuthSession } from '@/shared/auth/session';

describe('scriptsApi host-session requests', () => {
  const originalFetch = global.fetch;

  function jsonResponse(body: unknown, status = 200): Response {
    return {
      ok: status >= 200 && status < 300,
      status,
      statusText: status === 400 ? 'Bad Request' : 'OK',
      headers: {
        get: (name: string) =>
          name.toLowerCase() === 'content-type' ? 'application/json' : null,
      },
      json: async () => body,
      text: async () => JSON.stringify(body),
    } as Response;
  }

  beforeEach(() => {
    window.localStorage.clear();
    jest.spyOn(Date, 'now').mockReturnValue(1_700_000_000_000);
  });

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
    window.localStorage.clear();
  });

  it('injects a bearer token for protected Studio script endpoints', async () => {
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

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      headers: {
        get: (name: string) =>
          name.toLowerCase() === 'content-type' ? 'application/json' : null,
      },
      json: async () => ({
        scriptId: 'demo',
        validationSucceeded: true,
        findings: [],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await scriptsApi.validateDraft({
      scriptId: 'demo',
      scriptRevision: 'draft-1',
      source: 'public class Demo {}',
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/scripts/validate');
    expect(init?.credentials).toBe('same-origin');
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
  });

  it('runs a draft script through the scope-first draft-run endpoint', async () => {
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

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      headers: {
        get: (name: string) =>
          name.toLowerCase() === 'content-type' ? 'application/json' : null,
      },
      json: async () => ({
        accepted: true,
        scopeId: 'scope-1',
        scriptId: 'demo',
        scriptRevision: 'draft-1',
        definitionActorId: 'definition-1',
        runtimeActorId: 'runtime-1',
        runId: 'run-1',
        sourceHash: 'hash-1',
        commandTypeUrl: 'type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand',
        activityUrl: '/api/app/scripts/runtimes/runtime-1/activity',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await scriptsApi.runDraftScript({
      scopeId: 'scope-1',
      scriptId: 'demo',
      scriptRevision: 'draft-1',
      source: 'public class Demo {}',
      input: 'hello world',
      definitionActorId: 'definition-1',
      runtimeActorId: 'runtime-1',
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/scopes/scope-1/scripts/draft-run');
    expect(init?.method).toBe('POST');
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
    expect(JSON.parse(String(init?.body))).toEqual({
      scriptId: 'demo',
      scriptRevision: 'draft-1',
      source: 'public class Demo {}',
      input: 'hello world',
      definitionActorId: 'definition-1',
      runtimeActorId: 'runtime-1',
    });
  });

  it('falls back to script summaries when the backend rejects includeSource list requests', async () => {
    const scripts = [
      {
        available: true,
        scopeId: 'scope-1',
        script: {
          scopeId: 'scope-1',
          scriptId: 'demo',
          catalogActorId: 'catalog-1',
          definitionActorId: 'definition-1',
          activeRevision: 'rev-1',
          activeSourceHash: 'hash-1',
          updatedAt: '2026-03-27T00:00:00Z',
        },
        source: null,
      },
    ];
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(jsonResponse({ message: 'includeSource is unsupported' }, 400))
      .mockResolvedValueOnce(jsonResponse(scripts));
    global.fetch = fetchMock as typeof global.fetch;

    await expect(scriptsApi.listScripts('scope-1', true)).resolves.toEqual(scripts);

    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      '/api/scopes/scope-1/scripts?includeSource=true',
      expect.objectContaining({ credentials: 'same-origin' }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      '/api/scopes/scope-1/scripts?includeSource=false',
      expect.objectContaining({ credentials: 'same-origin' }),
    );
  });

  it('does not retry script list failures that are unrelated to includeSource compatibility', async () => {
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(jsonResponse({ message: 'Forbidden' }, 403));
    global.fetch = fetchMock as typeof global.fetch;

    await expect(scriptsApi.listScripts('scope-1', true)).rejects.toThrow('Forbidden');
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('requests script summaries directly when source is not required', async () => {
    const fetchMock = jest.fn().mockResolvedValueOnce(jsonResponse([]));
    global.fetch = fetchMock as typeof global.fetch;

    await expect(scriptsApi.listScripts('scope-1')).resolves.toEqual([]);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-1/scripts?includeSource=false',
      expect.objectContaining({ credentials: 'same-origin' }),
    );
  });

  it('reads runtime activity from the Studio app host routes', async () => {
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

    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      headers: {
        get: (name: string) =>
          name.toLowerCase() === 'content-type' ? 'application/json' : null,
      },
      json: async () => ({
        actorId: 'runtime-1',
        scriptId: 'demo',
        definitionActorId: 'definition-1',
        revision: 'draft-1',
        input: '',
        output: '',
        status: '',
        lastCommandId: '',
        notes: [],
        stateVersion: 1,
        lastEventId: 'event-1',
        updatedAt: '2026-03-27T00:00:00Z',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await scriptsApi.getRuntimeActivity('runtime-1');

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/app/scripts/runtimes/runtime-1/activity');
    expect(init?.credentials).toBe('same-origin');
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
  });

  it('collapses HTML error pages for Studio script endpoints', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: false,
      status: 502,
      statusText: 'Bad Gateway',
      text: async () => `<!DOCTYPE html>
<html lang="en-US">
  <head>
    <title>scripts gateway | 502: Bad gateway</title>
  </head>
  <body>
    <h1>Bad gateway</h1>
  </body>
</html>`,
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scriptsApi.validateDraft({
        scriptId: 'demo',
        scriptRevision: 'draft-1',
        source: 'public class Demo {}',
      }),
    ).rejects.toThrow('HTTP 502 Bad Gateway');
  });
});
