import path from 'node:path';
import { createConfig } from '@umijs/max/test';

const rootDir = __dirname;

const resolveFromRoot = (...segments: string[]): string =>
  path.join(rootDir, ...segments);

function buildModuleNameMapper(baseModuleNameMapper?: Record<string, unknown>) {
  return {
    ...(baseModuleNameMapper || {}),
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
}

function createProjectConfig(target: 'browser' | 'node') {
  const {
    testTimeout: _ignoredTestTimeout,
    watchman: _ignoredWatchman,
    ...baseConfig
  } = createConfig({
    target,
  });

  return {
    ...baseConfig,
    moduleNameMapper: buildModuleNameMapper(
      baseConfig.moduleNameMapper as Record<string, unknown> | undefined,
    ),
    openHandlesTimeout: 5000,
    rootDir,
    roots: ['<rootDir>/src', '<rootDir>/tests'],
    transformIgnorePatterns: ['/node_modules/(?!.*(?:lodash-es)/)'],
  };
}

const browserProjectConfig = createProjectConfig('browser');
const nodeProjectConfig = createProjectConfig('node');

const nodeTestFiles = [
  '<rootDir>/src/modules/studio/scripts/floatingLayout.test.ts',
  '<rootDir>/src/pages/MissionControl/runtimeAdapter.test.ts',
  '<rootDir>/src/pages/actors/actorPresentation.test.ts',
  '<rootDir>/src/pages/governance/components/governanceQuery.test.ts',
  '<rootDir>/src/pages/runs/runEventPresentation.test.ts',
  '<rootDir>/src/pages/scopes/components/resolvedScope.test.ts',
  '<rootDir>/src/pages/scopes/components/scopeQuery.test.ts',
  '<rootDir>/src/pages/services/components/serviceQuery.test.ts',
  '<rootDir>/src/pages/workflows/workflowPresentation.test.ts',
  '<rootDir>/src/shared/agui/customEventData.test.ts',
  '<rootDir>/src/shared/agui/sseFrameNormalizer.test.ts',
  '<rootDir>/src/shared/config/proxyConfig.test.ts',
  '<rootDir>/src/shared/datetime/dateTime.test.ts',
  '<rootDir>/src/shared/playground/stepSummary.test.ts',
  '<rootDir>/src/shared/studio/document.test.ts',
  '<rootDir>/src/shared/studio/invokeHistoryStore.test.ts',
  '<rootDir>/src/shared/studio/navigation.test.ts',
  '<rootDir>/src/shared/ui/aevatarWorkbench.test.ts',
  '<rootDir>/src/shared/workflows/catalogVisibility.test.ts',
] as const;

const browserIgnoredTestPatterns = nodeTestFiles.map((testPath) =>
  testPath
    .replace('<rootDir>/', '')
    .replace(/[.*+?^${}()|[\]\\]/g, '\\$&'),
);

const config: Record<string, unknown> = {
  testTimeout: 30000,
  projects: [
    {
      ...nodeProjectConfig,
      displayName: 'node',
      testMatch: [...nodeTestFiles],
    },
    {
      ...browserProjectConfig,
      displayName: 'jsdom',
      setupFiles: [
        ...((browserProjectConfig.setupFiles as string[] | undefined) || []),
        './tests/setupTests.jsx',
      ],
      setupFilesAfterEnv: [
        ...((browserProjectConfig.setupFilesAfterEnv as string[] | undefined) || []),
        './tests/setupAfterEnv.ts',
      ],
      testEnvironmentOptions: {
        ...(((browserProjectConfig.testEnvironmentOptions as Record<
          string,
          unknown
        >) || {})),
        url: 'http://localhost:8000',
      },
      testPathIgnorePatterns: [
        ...((browserProjectConfig.testPathIgnorePatterns as string[] | undefined) || []),
        ...browserIgnoredTestPatterns,
      ],
    },
  ],
};

export default config;
