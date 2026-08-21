/**
 * @name 代理的配置
 * @see 在生产环境 代理是无法生效的，所以这里没有生产环境的配置
 * -------------------------------
 * The agent cannot take effect in the production environment
 * so there is no configuration of the production environment
 * For details, please see
 * https://pro.ant.design/docs/deploy
 *
 * @doc https://umijs.org/docs/guides/proxy
 */
const apiTarget = process.env.AEVATAR_API_TARGET || 'http://127.0.0.1:5080';
const studioApiTarget = process.env.AEVATAR_STUDIO_API_TARGET || apiTarget;
const preserveAuthHost = process.env.AEVATAR_PROXY_PRESERVE_AUTH_HOST;

const buildProxyTarget = (target: string) => ({
  target,
  changeOrigin: true,
  ws: true,
});

const buildHostPreservingProxyTarget = (target: string) => ({
  target,
  changeOrigin: false,
  ws: true,
});

const shouldPreserveHost = (target: string) =>
  /^https?:\/\/(?:localhost|127\.0\.0\.1|\[::1\])(?::|\/|$)/i.test(target);

const buildAuthProxyTarget = (target: string) =>
  preserveAuthHost !== undefined
    ? preserveAuthHost !== 'false'
      ? buildHostPreservingProxyTarget(target)
      : buildProxyTarget(target)
    : shouldPreserveHost(target)
      ? buildHostPreservingProxyTarget(target)
      : buildProxyTarget(target);

const studioProxyEntries = [
  '/api/app',
  '/api/auth',
  '/api/connectors',
  '/api/editor',
  '/api/executions',
  '/api/roles',
  '/api/studio',
  '/api/user-config',
  '/api/workspace',
  '/api/workflow-templates',
].reduce<Record<string, ReturnType<typeof buildProxyTarget>>>(
  (entries, path) => {
    const proxyFactory =
      path === '/api/auth' ? buildAuthProxyTarget : buildProxyTarget;
    entries[`^${path}$`] = proxyFactory(studioApiTarget);
    entries[`${path}/`] = proxyFactory(studioApiTarget);
    return entries;
  },
  {},
);

const studioScopeProxyEntries = {
  '^/api/scopes/[^/]+/chat-history(?:/.*)?$': buildProxyTarget(studioApiTarget),
  '^/api/scopes/[^/]+/teams/[^/]+/invoke(?:/.*)?$': buildProxyTarget(apiTarget),
  '^/api/scopes/[^/]+/teams(?:/.*)?$': buildProxyTarget(studioApiTarget),
  '^/api/scopes/[^/]+/members$': buildProxyTarget(studioApiTarget),
  '^/api/scopes/[^/]+/members/[^/]+$': buildProxyTarget(studioApiTarget),
  '^/api/scopes/[^/]+/members/[^/]+/(?:binding(?:/.*)?|binding-runs(?:/.*)?|endpoints/[^/]+/contract)$':
    buildProxyTarget(studioApiTarget),
  '^/api/scopes/[^/]+/scripts/draft-run$': buildProxyTarget(studioApiTarget),
  '^/api/scopes/[^/]+/workflow/draft-run$': buildProxyTarget(studioApiTarget),
  '^/api/scopes/[^/]+/workflows:save-and-bind$':
    buildProxyTarget(studioApiTarget),
  '^/api/scopes/[^/]+/workflow-templates/[^/]+:instantiate$':
    buildProxyTarget(studioApiTarget),
  '^/api/scripts/validate$': buildProxyTarget(studioApiTarget),
  '^/api/scripts/generator$': buildProxyTarget(studioApiTarget),
  '^/api/workflows/generator$': buildProxyTarget(studioApiTarget),
};

const createProxyConfig = () => ({
  ...studioProxyEntries,
  ...studioScopeProxyEntries,
  '/api/': {
    target: apiTarget,
    changeOrigin: true,
    ws: true,
  },
});

export default {
  dev: createProxyConfig(),
  test: createProxyConfig(),
  pre: createProxyConfig(),
};
