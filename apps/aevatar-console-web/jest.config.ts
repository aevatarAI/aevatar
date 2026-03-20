import { configUmiAlias, createConfig } from '@umijs/max/test';

export default async (): Promise<Record<string, unknown>> => {
  const config = await configUmiAlias({
    ...createConfig({
      target: 'browser',
    }),
  });
  return {
    ...config,
    watchman: false,
    openHandlesTimeout: 5000,
    testEnvironmentOptions: {
      ...(config?.testEnvironmentOptions || {}),
      url: 'http://localhost:8000',
    },
    setupFiles: [...(config.setupFiles || []), './tests/setupTests.jsx'],
    setupFilesAfterEnv: [
      ...(config.setupFilesAfterEnv || []),
      './tests/setupAfterEnv.ts',
    ],
    globals: {
      ...config.globals,
      localStorage: null,
    },
  };
};
