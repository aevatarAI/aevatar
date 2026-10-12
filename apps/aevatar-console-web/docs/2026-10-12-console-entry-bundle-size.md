# Console entry bundle size

## Problem and scope

The `dev` baseline at `10ee0c919` eagerly included both complete application
language catalogs in `umi.js`. It also included Umi's default experimental
core-js proposals and a second CommonJS copy of the Ant Design locales.

This change loads the chosen language before mounting the existing Umi
providers and pages. A language switch downloads and registers its target
catalog before calling the existing `setLocale(locale, false)`. The existing
`umi_locale` preference, supported languages, auth/session behavior, routes,
and business messages retain their existing contracts.

## Loading and ownership

- `src/locales/en-US.ts` and `src/locales/zh-CN.ts` contain only the small
  loading/error/retry catalogs scanned by Umi's locale generator.
- `src/locales/messages/en-US.ts` and `src/locales/messages/zh-CN.ts` own the
  full catalogs. Add ordinary application messages to these files or their
  existing feature-specific message modules, not to the bootstrap files.
- `loadConsoleLocale` uses explicit dynamic imports and Umi's public
  `addLocale` API. It keeps in-flight and successful loads in a two-language
  cache. Failed imports or registrations are removed so retry is possible.
- `ConsoleLocaleGate`, installed through `rootContainer`, waits outside Umi's
  providers. It renders the existing page loader while downloading and a
  localized retry action on failure. It does not mount untranslated pages.
- `ConsoleLanguageSwitch` keeps the current language and page usable while
  the next catalog downloads. It ignores duplicate requests while pending,
  reports failure through `ConsoleToastProvider`, and allows another attempt.

There is no second locale context, locale storage key, or language state store.
The gate's state describes download readiness only. Ant Design uses the same
ESM locale modules as Umi, eliminating the extra `antd/lib/locale` copies.

The Chinese vNext catalog already imports English vNext messages as fallbacks.
That shared module remains a required chunk for both languages and is included
in the measurements below. The complete unused application catalog is excluded.

`polyfill.imports` now selects `core-js/stable`. Umi's existing browser targets
are unchanged; standards-based polyfills remain. The entry contains 187
core-js modules instead of 474, with no `esnext.*` proposal modules (previously
179). Unminified core-js module source in the entry drops from 437,816 to
163,185 bytes. Application source inspection found no uses of the removed
proposal APIs; a complete browser compatibility matrix was not exercised.

## Provider decision

The global Ant Design, Pro Components, Query, and toast providers remain.
The inner Pro provider receives the application's theme context and explicitly
selected Pro locale. Removing it based only on ProLayout having its own
provider could change component theming or localization. The provider and its
33 bundled Pro locale modules are a remaining optimization opportunity that
needs separate behavioral validation; they are not counted as savings here.

## Production bundle comparison

Both builds use the same worktree, dependency lockfile, Node runtime, and
`pnpm build:stats` command. Baseline output was copied before the edits. Gzip
sizes use Node's `gzipSync` per file, then sum the deduplicated files; they are
local estimates rather than HTTP transfer measurements. Values below are bytes.

| Resource | Before raw | After raw | Raw reduction | Before gzip | After gzip | Gzip reduction |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `umi.js` | 2,721,159 | 1,281,160 | 52.9% | 664,081 | 410,982 | 38.1% |
| Workflow detail, English | 3,610,854 | 2,760,415 | 23.6% | 929,668 | 778,544 | 16.3% |
| Workflow detail, Chinese | 3,610,854 | 2,966,127 | 17.9% | 929,668 | 803,656 | 13.6% |

The detail-page measurement includes HTML bootstrap scripts, the initial entry,
the selected language and its shared dependencies, Umi layout, editor route,
and resources selected by the actual HTML preload helper. It excludes fonts,
API responses, and user-opened panels. CSS remains 66,388 raw / 12,400 gzip
bytes. The JavaScript request count changes from 18 to 19 after bundling and
deduplication.

The selected catalog starts downloading after the entry executes, adding an
asynchronous startup dependency. Smaller transfer and parse volume do not
guarantee a faster FCP/LCP on every network. No Lighthouse, CPU trace,
Long Task, or production timing improvement is claimed by these size results.

`scripts/check-workflow-route-bundle.mjs` now includes the chosen language in
its cold-load graph. It rejects full catalogs in the initial entry, the unused
full catalog in the cold route graph, experimental core-js proposals, and
duplicate CommonJS Ant Design locales. Existing checks still exclude 17
unrelated pages/closed panels and verify four actual preload route selections.

## Verification

From `apps/aevatar-console-web/`:

```bash
pnpm tsc
pnpm build:stats
pnpm exec jest --runInBand --runTestsByPath src/shared/i18n/createLocaleCatalogLoader.test.ts src/locales/catalog.test.ts src/locales/hardcodedCopyAudit.test.ts --listTests
pnpm exec jest --runInBand --runTestsByPath src/shared/i18n/createLocaleCatalogLoader.test.ts src/locales/catalog.test.ts src/locales/hardcodedCopyAudit.test.ts
node scripts/check-workflow-route-bundle.mjs dist/bundle-stats.json en-US
node scripts/check-workflow-route-bundle.mjs dist/bundle-stats.json zh-CN
python3 docs/design-baselines/workflow-activity-vnext/verify-baseline.py
```

These passed. The explicit test selection contains three files and 18 passing
tests: catalog consistency, source copy auditing, selected-language loading,
in-flight deduplication, successful caching, download failure/retry, and
registration failure/retry. These are pure logic/catalog tests even where the
existing Jest project uses jsdom. Dependency discovery was deliberately skipped
for the global entry, shared provider, locale catalogs, and shared Umi mock
because they have package-wide fan-out.

Changed-file `pnpm exec biome lint` passed for the 21 affected TypeScript and
JavaScript files, with two existing warnings in `hardcodedCopyAudit.test.ts`
(an intentional template-string fixture and an unused audit helper). From the
repository root, `bash tools/ci/test_stability_guards.sh`,
`bash tools/docs/lint.sh`, and `git diff --check` passed.

Rendered coverage was added in `ConsoleLocaleGate.test.tsx` for startup
failure/retry and in `ConsoleHeaderActions.test.tsx` for delayed switching,
duplicate clicks, failure, and retry. The existing successful-switch test now
awaits the asynchronous transition. These component integration tests were
typechecked but not executed: the current task's test policy excludes local
integration runs. Full frontend tests, browser E2E/smoke tests, and Lighthouse
were not run. CI or a separately authorized focused integration run still needs
to validate the rendered scenarios and shared Umi mock consumers, including the
inspector test whose catalog imports moved.

## Design baseline declaration

```text
Design baseline:
  apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/
Primary design:
  aevatar-workflow-activity-vnext.excalidraw
Design SHA-256:
  30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de
Contract specification:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-04-workflow-activity-vnext-design.md
User paths:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-04-workflow-activity-vnext-user-paths.md
Authentication and localization:
  Existing Aevatar login, callback, session, returnTo, and Umi locale logic.
Production data source:
  Real APIs and API-acknowledged user actions only; no mock fallback.
Baseline integrity:
  python3 apps/aevatar-console-web/docs/design-baselines/
  workflow-activity-vnext/verify-baseline.py
```

The verifier passed with the exact 17-frame primary inventory and unchanged
deterministic generator output. Schedule functionality and the visual baseline
are outside this change.
