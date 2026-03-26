import { studioApi } from './api';
import { persistAuthSession } from '@/shared/auth/session';

describe('studioApi host-session requests', () => {
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

  it('injects the NyxID bearer token for Studio host endpoints', async () => {
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
      json: async () => ({
        enabled: true,
        authenticated: true,
        providerDisplayName: 'NyxID',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await studioApi.getAuthSession();

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/auth/me');
    expect(init?.credentials).toBe('same-origin');
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
  });

  it('loads template workflows from the Studio host using bearer auth', async () => {
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
      json: async () => ({
        catalog: {
          name: 'published-demo',
          description: 'Published demo workflow',
          category: '',
          group: '',
          groupLabel: '',
          sortOrder: 0,
          source: 'catalog',
          sourceLabel: 'Published templates',
          showInLibrary: true,
          isPrimitiveExample: false,
          requiresLlmProvider: false,
          primitives: [],
        },
        yaml: 'name: published-demo\nsteps: []\n',
        definition: {
          name: 'published-demo',
          description: 'Published demo workflow',
          closedWorldMode: false,
          roles: [],
          steps: [],
        },
        edges: [],
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await studioApi.getTemplateWorkflow('published-demo');

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/workflows/published-demo');
    expect(init?.credentials).toBe('same-origin');
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
  });

  it('binds a saved script to the default service using the scope binding endpoint', async () => {
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
      json: async () => ({
        scopeId: 'scope-1',
        displayName: 'script-1',
        revisionId: 'rev-1',
        workflowName: 'script-1',
        definitionActorIdPrefix: 'definition',
        expectedActorId: 'definition-scope-1',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await studioApi.bindScopeScript({
      scopeId: 'scope-1',
      displayName: 'script-1',
      scriptId: 'script-1',
      scriptRevision: 'rev-1',
      revisionId: 'rev-1',
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe('/api/scopes/scope-1/binding');
    expect(init?.method).toBe('PUT');
    expect(new Headers(init?.headers).get('Authorization')).toBe(
      'Bearer access-token',
    );
    expect(JSON.parse(String(init?.body))).toEqual({
      implementationKind: 'script',
      displayName: 'script-1',
      scriptId: 'script-1',
      scriptRevision: 'rev-1',
      revisionId: 'rev-1',
    });
  });
});
