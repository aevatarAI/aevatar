describe('proxy config', () => {
  const originalApiTarget = process.env.AEVATAR_API_TARGET;
  const originalStudioApiTarget = process.env.AEVATAR_STUDIO_API_TARGET;

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

    jest.resetModules();
  });

  it('routes scope script draft runs to the Studio host', () => {
    process.env.AEVATAR_API_TARGET = 'http://127.0.0.1:5080';
    process.env.AEVATAR_STUDIO_API_TARGET = 'http://127.0.0.1:5180';

    const proxyModule = require('../../../config/proxy');
    const devProxy = proxyModule.default.dev as Record<
      string,
      {
        target: string;
        changeOrigin: boolean;
        ws: boolean;
      }
    >;

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
});
