describe('proxy config', () => {
  const originalApiTarget = process.env.AEVATAR_API_TARGET;
  const originalStudioApiTarget = process.env.AEVATAR_STUDIO_API_TARGET;
  const originalProxyPreserveAuthHost =
    process.env.AEVATAR_PROXY_PRESERVE_AUTH_HOST;
  const originalProxySecure = process.env.AEVATAR_PROXY_SECURE;
  type ProxyEntry = {
    target: string;
    changeOrigin: boolean;
    secure: boolean;
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

    if (originalProxySecure === undefined) {
      delete process.env.AEVATAR_PROXY_SECURE;
    } else {
      process.env.AEVATAR_PROXY_SECURE = originalProxySecure;
    }

    if (originalProxyPreserveAuthHost === undefined) {
      delete process.env.AEVATAR_PROXY_PRESERVE_AUTH_HOST;
    } else {
      process.env.AEVATAR_PROXY_PRESERVE_AUTH_HOST =
        originalProxyPreserveAuthHost;
    }

    jest.resetModules();
  });

  it('keeps local backend targets by default', () => {
    delete process.env.AEVATAR_API_TARGET;
    delete process.env.AEVATAR_STUDIO_API_TARGET;
    delete process.env.AEVATAR_PROXY_SECURE;
    delete process.env.AEVATAR_PROXY_PRESERVE_AUTH_HOST;

    const proxyModule = require('../../../config/proxy');
    const devProxy = proxyModule.default.dev as Record<string, ProxyEntry>;

    expect(resolveProxyEntry(devProxy, '/api/auth/me')).toEqual({
      target: 'http://127.0.0.1:5080',
      changeOrigin: false,
      secure: true,
      ws: true,
    });
    expect(
      resolveProxyEntry(devProxy, '/api/scopes/scope-1/gagent/draft-run'),
    ).toEqual({
      target: 'http://127.0.0.1:5080',
      changeOrigin: true,
      secure: true,
      ws: true,
    });
    expect(devProxy['/api/']).toEqual({
      target: 'http://127.0.0.1:5080',
      changeOrigin: true,
      secure: true,
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
      secure: true,
      ws: true,
    });
    expect(devProxy['/api/']).toEqual({
      target: 'http://127.0.0.1:5080',
      changeOrigin: true,
      secure: true,
      ws: true,
    });
  });

  it('routes scope GAgent draft runs to the Studio host', () => {
    process.env.AEVATAR_API_TARGET = 'http://127.0.0.1:5080';
    process.env.AEVATAR_STUDIO_API_TARGET = 'http://127.0.0.1:5180';

    const proxyModule = require('../../../config/proxy');
    const devProxy = proxyModule.default.dev as Record<string, ProxyEntry>;

    expect(
      resolveProxyEntry(devProxy, '/api/scopes/scope-1/gagent/draft-run'),
    ).toEqual({
      target: 'http://127.0.0.1:5180',
      changeOrigin: true,
      secure: true,
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
      secure: true,
      ws: true,
    });
    expect(
      resolveProxyEntry(devProxy, '/api/scopes/scope-1/teams/t-alpha/archive'),
    ).toEqual({
      target: 'http://127.0.0.1:5180',
      changeOrigin: true,
      secure: true,
      ws: true,
    });
    expect(
      resolveProxyEntry(devProxy, '/api/scopes/scope-1/members/m-alpha/runs'),
    ).toEqual({
      target: 'http://127.0.0.1:5080',
      changeOrigin: true,
      secure: true,
      ws: true,
    });
  });

  it('can change origin for auth when local dev proxies to the hosted backend', () => {
    process.env.AEVATAR_API_TARGET = 'https://aevatar-console-backend-api.aevatar.ai';
    process.env.AEVATAR_STUDIO_API_TARGET = 'https://aevatar-console-backend-api.aevatar.ai';
    process.env.AEVATAR_PROXY_SECURE = 'false';
    process.env.AEVATAR_PROXY_PRESERVE_AUTH_HOST = 'false';

    const proxyModule = require('../../../config/proxy');
    const devProxy = proxyModule.default.dev as Record<string, ProxyEntry>;

    expect(resolveProxyEntry(devProxy, '/api/auth/me')).toEqual({
      target: 'https://aevatar-console-backend-api.aevatar.ai',
      changeOrigin: true,
      secure: false,
      ws: true,
    });
  });
});
