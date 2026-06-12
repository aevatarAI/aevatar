describe('proxy config', () => {
  const originalApiTarget = process.env.AEVATAR_API_TARGET;
  const originalStudioApiTarget = process.env.AEVATAR_STUDIO_API_TARGET;
  const originalPreserveAuthHost =
    process.env.AEVATAR_PROXY_PRESERVE_AUTH_HOST;
  type ProxyEntry = {
    target: string;
    changeOrigin: boolean;
    ws: boolean;
  };

  const resolveProxyEntry = (
    proxy: Record<string, ProxyEntry>,
    pathname: string,
  ): ProxyEntry | undefined => {
    for (const [pattern, entry] of Object.entries(proxy)) {
      if (pattern.startsWith('^') && new RegExp(pattern).test(pathname)) {
        return entry;
      }

      if (pattern.endsWith('/') && pathname.startsWith(pattern)) {
        return entry;
      }

      if (pattern === pathname) {
        return entry;
      }
    }

    return undefined;
  };

  afterEach(() => {
    if (originalApiTarget === undefined) {
      delete process.env.AEVATAR_API_TARGET;
    } else {
      process.env.AEVATAR_API_TARGET = originalApiTarget;
    }

    if (originalStudioApiTarget === undefined) {
      delete process.env.AEVATAR_STUDIO_API_TARGET;
    } else {
      process.env.AEVATAR_STUDIO_API_TARGET = originalStudioApiTarget;
    }

    if (originalPreserveAuthHost === undefined) {
      delete process.env.AEVATAR_PROXY_PRESERVE_AUTH_HOST;
    } else {
      process.env.AEVATAR_PROXY_PRESERVE_AUTH_HOST =
        originalPreserveAuthHost;
    }

    jest.resetModules();
  });

  it('keeps local auth proxy host preservation by default', () => {
    process.env.AEVATAR_API_TARGET = 'http://127.0.0.1:5080';
    process.env.AEVATAR_STUDIO_API_TARGET = 'http://127.0.0.1:5080';
    delete process.env.AEVATAR_PROXY_PRESERVE_AUTH_HOST;

    const proxyModule = require('../../../config/proxy');
    const devProxy = proxyModule.default.dev as Record<string, ProxyEntry>;

    expect(resolveProxyEntry(devProxy, '/api/auth/me')).toEqual({
      target: 'http://127.0.0.1:5080',
      changeOrigin: false,
      ws: true,
    });
  });

  it('allows hosted backend auth proxying from env.local', () => {
    process.env.AEVATAR_API_TARGET =
      'https://aevatar-console-backend-api.aevatar.ai';
    process.env.AEVATAR_STUDIO_API_TARGET =
      'https://aevatar-console-backend-api.aevatar.ai';
    process.env.AEVATAR_PROXY_PRESERVE_AUTH_HOST = 'false';

    const proxyModule = require('../../../config/proxy');
    const devProxy = proxyModule.default.dev as Record<string, ProxyEntry>;

    expect(resolveProxyEntry(devProxy, '/api/auth/me')).toEqual({
      target: 'https://aevatar-console-backend-api.aevatar.ai',
      changeOrigin: true,
      ws: true,
    });
    expect(resolveProxyEntry(devProxy, '/api/scopes/scope-1/teams')).toEqual({
      target: 'https://aevatar-console-backend-api.aevatar.ai',
      changeOrigin: true,
      ws: true,
    });
    expect(
      resolveProxyEntry(devProxy, '/api/scopes/scope-1/members/m-alpha/runs'),
    ).toEqual({
      target: 'https://aevatar-console-backend-api.aevatar.ai',
      changeOrigin: true,
      ws: true,
    });
  });

  it('routes scope script draft runs to the Studio host', () => {
    process.env.AEVATAR_API_TARGET = 'http://127.0.0.1:5080';
    process.env.AEVATAR_STUDIO_API_TARGET = 'http://127.0.0.1:5180';

    const proxyModule = require('../../../config/proxy');
    const devProxy = proxyModule.default.dev as Record<string, ProxyEntry>;

    expect(devProxy['^/api/scopes/[^/]+/scripts/draft-run$']).toEqual({
      target: 'http://127.0.0.1:5180',
      changeOrigin: true,
      ws: true,
    });
    expect(devProxy['/api/']).toEqual({
      target: 'http://127.0.0.1:5080',
      changeOrigin: true,
      ws: true,
    });
  });

  it('routes scope workflow draft runs to the Studio host', () => {
    process.env.AEVATAR_API_TARGET = 'http://127.0.0.1:5080';
    process.env.AEVATAR_STUDIO_API_TARGET = 'http://127.0.0.1:5180';

    const proxyModule = require('../../../config/proxy');
    const devProxy = proxyModule.default.dev as Record<string, ProxyEntry>;

    expect(devProxy['^/api/scopes/[^/]+/workflow/draft-run$']).toEqual({
      target: 'http://127.0.0.1:5180',
      changeOrigin: true,
      ws: true,
    });
    expect(
      resolveProxyEntry(devProxy, '/api/scopes/scope-1/workflow/draft-run'),
    ).toEqual({
      target: 'http://127.0.0.1:5180',
      changeOrigin: true,
      ws: true,
    });
    expect(resolveProxyEntry(devProxy, '/api/scopes/scope-1/members/m-alpha/runs')).toEqual({
      target: 'http://127.0.0.1:5080',
      changeOrigin: true,
      ws: true,
    });
  });

  it('routes scope team endpoints to the Studio host without stealing runtime member routes', () => {
    process.env.AEVATAR_API_TARGET = 'http://127.0.0.1:5080';
    process.env.AEVATAR_STUDIO_API_TARGET = 'http://127.0.0.1:5180';

    const proxyModule = require('../../../config/proxy');
    const devProxy = proxyModule.default.dev as Record<string, ProxyEntry>;

    expect(resolveProxyEntry(devProxy, '/api/scopes/scope-1/teams')).toEqual({
      target: 'http://127.0.0.1:5180',
      changeOrigin: true,
      ws: true,
    });
    expect(resolveProxyEntry(devProxy, '/api/scopes/scope-1/members')).toEqual({
      target: 'http://127.0.0.1:5180',
      changeOrigin: true,
      ws: true,
    });
    expect(resolveProxyEntry(devProxy, '/api/scopes/scope-1/members/m-alpha')).toEqual({
      target: 'http://127.0.0.1:5180',
      changeOrigin: true,
      ws: true,
    });
    expect(
      resolveProxyEntry(devProxy, '/api/scopes/scope-1/members/m-alpha/binding'),
    ).toEqual({
      target: 'http://127.0.0.1:5180',
      changeOrigin: true,
      ws: true,
    });
    expect(
      resolveProxyEntry(
        devProxy,
        '/api/scopes/scope-1/members/m-alpha/endpoints/chat/contract',
      ),
    ).toEqual({
      target: 'http://127.0.0.1:5180',
      changeOrigin: true,
      ws: true,
    });
    expect(
      resolveProxyEntry(devProxy, '/api/scopes/scope-1/teams/t-alpha/archive'),
    ).toEqual({
      target: 'http://127.0.0.1:5180',
      changeOrigin: true,
      ws: true,
    });
    expect(
      resolveProxyEntry(devProxy, '/api/scopes/scope-1/teams/t-alpha/invoke/chat:stream'),
    ).toEqual({
      target: 'http://127.0.0.1:5080',
      changeOrigin: true,
      ws: true,
    });
    expect(
      resolveProxyEntry(devProxy, '/api/scopes/scope-1/members/m-alpha/runs'),
    ).toEqual({
      target: 'http://127.0.0.1:5080',
      changeOrigin: true,
      ws: true,
    });
  });

  it('preserves auth host only for local Studio targets', () => {
    process.env.AEVATAR_API_TARGET = 'http://127.0.0.1:5080';
    process.env.AEVATAR_STUDIO_API_TARGET = 'http://127.0.0.1:5180';

    const localProxyModule = require('../../../config/proxy');
    const localDevProxy = localProxyModule.default.dev as Record<string, ProxyEntry>;

    expect(resolveProxyEntry(localDevProxy, '/api/auth/me')).toEqual({
      target: 'http://127.0.0.1:5180',
      changeOrigin: false,
      ws: true,
    });

    jest.resetModules();
    process.env.AEVATAR_STUDIO_API_TARGET =
      'https://aevatar-console-backend-api.aevatar.ai';

    const remoteProxyModule = require('../../../config/proxy');
    const remoteDevProxy = remoteProxyModule.default.dev as Record<string, ProxyEntry>;

    expect(resolveProxyEntry(remoteDevProxy, '/api/auth/me')).toEqual({
      target: 'https://aevatar-console-backend-api.aevatar.ai',
      changeOrigin: true,
      ws: true,
    });
  });
});
