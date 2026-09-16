import { defineConfig } from '@playwright/test';

const benchmarkBaseUrl = 'http://127.0.0.1:4174';

export default defineConfig({
  expect: {
    timeout: 10_000,
  },
  fullyParallel: false,
  outputDir: 'artifacts/workflow-canvas-benchmark/test-results',
  reporter: [['line']],
  testDir: './performance',
  testMatch: 'workflowCanvas.performance.spec.ts',
  timeout: 15 * 60_000,
  use: {
    baseURL: benchmarkBaseUrl,
    browserName: 'chromium',
    channel: 'chrome',
    headless: true,
    launchOptions: {
      args: ['--enable-precise-memory-info'],
    },
    viewport: {
      height: 900,
      width: 1440,
    },
  },
  webServer: {
    command: 'pnpm benchmark:workflow-canvas:serve',
    reuseExistingServer: false,
    timeout: 120_000,
    url: `${benchmarkBaseUrl}/workflow-canvas-benchmark?nodes=100&minimap=1&visible=1`,
  },
  workers: 1,
});
