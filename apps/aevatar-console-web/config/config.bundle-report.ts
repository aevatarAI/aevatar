import { defineConfig } from '@umijs/max';

// Opt-in production stats; no analyzer server and no module source/credentials.
export default defineConfig({
  analyze: {
    analyzerMode: 'disabled',
    generateStatsFile: true,
    statsFilename: 'bundle-stats.json',
    statsOptions: {
      all: false,
      assets: true,
      entrypoints: true,
      chunks: true,
      chunkModules: true,
      chunkGroups: true,
      nestedModules: true,
      dependentModules: true,
      orphanModules: true,
      ids: true,
      chunkModulesSpace: Number.POSITIVE_INFINITY,
      nestedModulesSpace: Number.POSITIVE_INFINITY,
    },
  },
});
