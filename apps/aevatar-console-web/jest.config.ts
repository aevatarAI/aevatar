import path from 'node:path';
import { createConfig } from '@umijs/max/test';

const baseConfig = createConfig({
  target: 'browser',
});

const rootDir = __dirname;

const resolveFromRoot = (...segments: string[]): string =>
  path.join(rootDir, ...segments);

const moduleNameMapper = {
  ...(baseConfig.moduleNameMapper || {}),
  '^@ant-design/icons$': resolveFromRoot(
    'tests',
    'mocks',
    'antDesignIcons.js',
  ),
  '^@monaco-editor/react$': resolveFromRoot(
    'tests',
    'mocks',
    'monacoEditor.tsx',
  ),
  '^@ant-design/pro-components$': resolveFromRoot(
    'tests',
    'mocks',
    'proComponents.tsx',
  ),
  '^antd/es/(.*)$': resolveFromRoot('node_modules', 'antd', 'lib', '$1'),
  '^@ant-design/icons/es/(.*)$': resolveFromRoot(
    'node_modules',
    '@ant-design',
    'icons',
    'lib',
    '$1',
  ),
  '^@rc-component/([^/]+)/es/(.*)$': resolveFromRoot(
    'node_modules',
    '@rc-component',
    '$1',
    'lib',
    '$2',
  ),
  '^rc-([^/]+)/es/(.*)$': resolveFromRoot(
    'node_modules',
    'rc-$1',
    'lib',
    '$2',
  ),
  '^@/(.*)$': resolveFromRoot('src', '$1'),
  '^@$': resolveFromRoot('src'),
  '^@@/(.*)$': resolveFromRoot('src', '.umi', '$1'),
  '^@@$': resolveFromRoot('src', '.umi'),
  '^@@test/(.*)$': resolveFromRoot('src', '.umi-test', '$1'),
  '^@@test$': resolveFromRoot('src', '.umi-test'),
};

const config: Record<string, unknown> = {
  ...baseConfig,
  moduleNameMapper,
  openHandlesTimeout: 5000,
  roots: ['<rootDir>/src', '<rootDir>/tests'],
  setupFiles: [...(baseConfig.setupFiles || []), './tests/setupTests.jsx'],
  setupFilesAfterEnv: [
    ...(baseConfig.setupFilesAfterEnv || []),
    './tests/setupAfterEnv.ts',
  ],
  testEnvironmentOptions: {
    ...((baseConfig.testEnvironmentOptions as Record<string, unknown>) || {}),
    url: 'http://localhost:8000',
  },
  transformIgnorePatterns: ['/node_modules/(?!.*(?:lodash-es)/)'],
  watchman: false,
};

export default config;
