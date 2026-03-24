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
const configurationApiTarget =
  process.env.AEVATAR_CONFIGURATION_API_TARGET || 'http://127.0.0.1:6688';
const studioApiTarget =
  process.env.AEVATAR_STUDIO_API_TARGET || apiTarget;

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

const studioProxyEntries = [
  '/api/app',
  '/api/auth',
  '/api/connectors',
  '/api/editor',
  '/api/executions',
  '/api/roles',
  '/api/settings',
  '/api/workspace',
].reduce<Record<string, ReturnType<typeof buildProxyTarget>>>((entries, path) => {
  const proxyFactory = path === '/api/auth'
    ? buildHostPreservingProxyTarget
    : buildProxyTarget;
  entries[`^${path}$`] = proxyFactory(studioApiTarget);
  entries[`${path}/`] = proxyFactory(studioApiTarget);
  return entries;
}, {});

const createProxyConfig = () => ({
  '/api/configuration/': {
    target: configurationApiTarget,
    changeOrigin: true,
    ws: true,
  },
  ...studioProxyEntries,
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
