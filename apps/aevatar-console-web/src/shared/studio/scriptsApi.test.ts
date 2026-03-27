import { scriptsApi } from './scriptsApi';
import { persistAuthSession } from '@/shared/auth/session';

describe('scriptsApi host-session requests', () => {
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
    expect(input).toBe('/api/app/scripts/validate');
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
        readModelUrl: '/api/app/scripts/runtimes/runtime-1/readmodel',
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
