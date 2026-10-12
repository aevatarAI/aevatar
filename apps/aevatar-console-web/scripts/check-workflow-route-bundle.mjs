import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { runInNewContext } from 'node:vm';
import { gzipSync } from 'node:zlib';

const statsPath = resolve(process.argv[2] ?? 'dist/bundle-stats.json');
const locale = process.argv[3] ?? 'en-US';
assert(['en-US', 'zh-CN'].includes(locale), 'Expected en-US or zh-CN locale.');
const stats = JSON.parse(readFileSync(statsPath, 'utf8'));
const outputDirectory = dirname(statsPath);
const html = readFileSync(resolve(outputDirectory, 'index.html'), 'utf8');
const verificationOrigin = 'https://bundle-check.invalid';
function localAssetFile(url) {
  const parsed = new URL(url, verificationOrigin);
  assert.equal(
    parsed.origin,
    verificationOrigin,
    'External bootstrap assets require a separate transfer measurement.',
  );
  return decodeURIComponent(parsed.pathname).replace(/^\/+/, '');
}
function attribute(tag, name) {
  return tag.match(new RegExp(`\\b${name}=["']([^"']+)["']`, 'i'))?.[1];
}
const htmlScripts = [...html.matchAll(/<script\b[^>]*>/gi)]
  .map(([tag]) => attribute(tag, 'src'))
  .filter(Boolean)
  .map(localAssetFile);
const htmlStyles = [...html.matchAll(/<link\b[^>]*>/gi)]
  .filter(([tag]) => attribute(tag, 'rel') === 'stylesheet')
  .map(([tag]) => attribute(tag, 'href'))
  .filter(Boolean)
  .map(localAssetFile);
const preloadHelpers = htmlScripts.filter((file) =>
  /(?:^|\/)preload_helper(?:\.[a-f0-9]+)?\.js$/.test(file),
);
assert.equal(
  preloadHelpers.length,
  1,
  'Expected one Umi HTML preload helper; review bootstrap resource discovery.',
);
const preloadHelper = readFileSync(
  resolve(outputDirectory, preloadHelpers[0]),
  'utf8',
);

// Execute only the generated resource selector, with DOM writes captured and
// no browser or network. This also catches a catch-all preloading before a
// more specific parameterized route, which chunk-group inspection alone misses.
function preloadedFiles(pathname) {
  const urls = [];
  runInNewContext(
    preloadHelper,
    {
      location: { pathname },
      document: {
        createElement: () => ({ setAttribute() {} }),
        head: {
          appendChild(element) {
            const url = element.src ?? element.href;
            assert.equal(typeof url, 'string', 'Unknown preload resource.');
            urls.push(url);
          },
        },
      },
    },
    { timeout: 1000 },
  );
  return [...new Set(urls.map(localAssetFile))];
}

const routeGroup =
  stats.namedChunkGroups[
    'p__workflow-activity-vnext__routes__WorkflowEditorRoute'
  ];
assert(routeGroup, 'Workflow editor must have its own Umi route chunk group.');
const layoutGroup =
  stats.namedChunkGroups.t__plugin_layout__Layout ??
  stats.namedChunkGroups['t__plugin-layout__Layout'];
assert(layoutGroup, 'Expected Umi layout chunk group; review the load graph.');
const bootstrapCases = [
  {
    pathname: '/scopes/perf-scope/workflows/perf-workflow',
    group: routeGroup,
    hasLayout: true,
  },
  {
    pathname: '/scopes/perf-scope/workflows/new',
    group:
      stats.namedChunkGroups[
        'p__workflow-activity-vnext__routes__NewWorkflowRoute'
      ],
    hasLayout: true,
  },
  {
    pathname: '/scopes/perf-scope/workflows/new/templates',
    group:
      stats.namedChunkGroups[
        'p__workflow-activity-vnext__routes__WorkflowTemplatesRoute'
      ],
    hasLayout: true,
  },
  {
    pathname: '/scopes/perf-scope/not-a-route',
    group: stats.namedChunkGroups.p__404,
    hasLayout: false,
  },
];
for (const { pathname, group, hasLayout } of bootstrapCases) {
  assert(group, `Missing route chunk group for ${pathname}.`);
  const expected = [
    ...new Set(
      [group, ...(hasLayout ? [layoutGroup] : [])].flatMap((entry) =>
        entry.assets.map((asset) => asset.name),
      ),
    ),
  ].sort();
  assert.deepEqual(
    preloadedFiles(pathname).sort(),
    expected,
    `HTML helper must preload the matching route and its layout: ${pathname}`,
  );
}
const initialChunks = stats.chunks.filter((chunk) => chunk.initial);
assert(initialChunks.length > 0, 'Missing initial chunks in production stats.');
const localeGroup = stats.namedChunkGroups[`console-locale-${locale}`];
assert(
  localeGroup,
  'Expected a separate chunk group for the selected language.',
);
const requiredIds = new Set([
  ...initialChunks.map((chunk) => chunk.id),
  ...localeGroup.chunks,
  ...layoutGroup.chunks,
  ...routeGroup.chunks,
]);
const requiredChunks = stats.chunks.filter((chunk) =>
  requiredIds.has(chunk.id),
);
assert.equal(
  requiredChunks.length,
  requiredIds.size,
  'Missing required chunks.',
);

const modules = new Set();
function collectModules(entries, target = modules) {
  for (const entry of entries ?? []) {
    // Incomplete stats must not turn an uninspected bundle into a passing check.
    assert(
      !entry.filteredChildren || entry.type === 'runtime modules',
      'Module stats were truncated; use the build:stats configuration.',
    );
    if (entry.nameForCondition) {
      target.add(entry.nameForCondition.replaceAll('\\', '/'));
    } else if (entry.name) {
      target.add(
        entry.name.replaceAll('\\', '/').replace(/ \+ \d+ modules$/, ''),
      );
    }
    collectModules(entry.modules, target);
    collectModules(entry.children, target);
  }
}
for (const chunk of requiredChunks) collectModules(chunk.modules);
const includesModule = (suffix) =>
  [...modules].some((name) => name.endsWith(suffix));
const initialModules = new Set();
for (const chunk of initialChunks)
  collectModules(chunk.modules, initialModules);
assert(
  ![...initialModules].some((name) =>
    /\/core-js\/modules\/esnext\./.test(name),
  ),
  'Experimental core-js proposals must not return to the initial entry.',
);
assert(
  ![...initialModules].some((name) =>
    /\/antd\/lib\/locale\/(en_US|zh_CN)\.js$/.test(name),
  ),
  'Use the same ESM Ant Design locales as Umi to avoid duplicate catalogs.',
);
assert(
  ![...initialModules].some((name) =>
    /\/locales\/messages\/(en-US|zh-CN)\.ts$/.test(name),
  ),
  'Full application language catalogs must stay out of the initial entry.',
);
assert(
  includesModule(`/locales/messages/${locale}.ts`),
  'The cold load measurement must include the full selected-language catalog.',
);
assert(
  !includesModule(
    `/locales/messages/${locale === 'en-US' ? 'zh-CN' : 'en-US'}.ts`,
  ),
  'The unused language must not be downloaded for the initial route.',
);
assert(
  includesModule('/workflows/WorkflowEditorPage.tsx') &&
    includesModule('/shared/graphs/GraphCanvas.tsx'),
  'The initial route must still contain the working editor and default canvas.',
);

const deferredModules = [
  '/workflows/WorkflowsPage.tsx',
  '/workflows/NewWorkflowPage.tsx',
  '/workflows/WorkflowTemplatesPage.tsx',
  '/activity/ActivityPage.tsx',
  '/activity/RunDetailPage.tsx',
  '/channels/ChannelsPage.tsx',
  '/channels/ChannelDetailsPage.tsx',
  '/channels/ChannelConfigurationPage.tsx',
  '/settings/SettingsPage.tsx',
  '/workflows/WorkflowScheduleSurface.tsx',
  '/workflows/WorkflowNodeInspector.tsx',
  '/shared/workflows/WorkflowRunInputPanel.tsx',
  '/shared/workflows/WorkflowExecutionLogsPanel.tsx',
  '/components/WorkflowStudioNodeLibrary.tsx',
  '/layouts/liveOpsAttention.ts',
  '/pages/MissionControl/runtimeAdapter.ts',
  '/pages/MissionControl/services/api.ts',
];
assert.deepEqual(
  deferredModules.filter(includesModule),
  [],
  'Unrelated pages or closed editor panels leaked into the initial load graph.',
);

const files = [
  ...new Set([
    ...requiredChunks.flatMap((chunk) => chunk.files),
    ...htmlScripts,
    ...htmlStyles,
    ...preloadedFiles(bootstrapCases[0].pathname),
  ]),
];
function measure(selected) {
  return selected.reduce(
    (total, file) => {
      const bytes = readFileSync(resolve(outputDirectory, file));
      return {
        raw: total.raw + bytes.length,
        gzip: total.gzip + gzipSync(bytes).length,
      };
    },
    { raw: 0, gzip: 0 },
  );
}
const jsFiles = files.filter((file) => file.endsWith('.js'));
const cssFiles = files.filter((file) => file.endsWith('.css'));
console.log(
  JSON.stringify(
    {
      description:
        'Cold workflow detail: HTML bootstrap + initial entry + selected language + Umi layout + editor route + actual preloads, deduplicated',
      locale,
      javascript: { files: jsFiles.length, ...measure(jsFiles) },
      css: { files: cssFiles.length, ...measure(cssFiles) },
      excludedModules: deferredModules.length,
      verifiedPreloadRoutes: bootstrapCases.length,
      note: 'Local gzip estimates; excludes fonts, API data, and user-opened panels. Not a Lighthouse timing measurement.',
    },
    null,
    2,
  ),
);
