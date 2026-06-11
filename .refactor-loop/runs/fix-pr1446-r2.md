# Fix PR 1446 r2

## Context

- PR: 1446
- Branch: `refactor/issue1444-first`
- Failed check: `console-web` in run `26685830195`
- Failed step: `Test console-web`

## Root Cause

`ScriptsWorkbenchPage.test.tsx` still asserted the old single-probe behavior for save observation and promotion catalog reads. The current page correctly probes read-model visibility multiple times before reporting an accepted-but-not-observed operation as pending. In CI this produced four failures:

- The save pending test could not find the pending observation notice.
- The stale promotion test could not find the pending catalog notice.
- The missing/rejected promotion tests expected exactly one `getScriptCatalog` call but the page made repeated visibility probes.

## Fix

- Added `waitForObservationProbeTick` to `ScriptsWorkbenchPage` so production keeps the existing 250 ms observation cadence while tests can inject a deterministic immediate tick.
- Updated the save pending test to keep `observeSaveScript` pending across the full 8-probe window.
- Updated promotion pending tests to assert user-visible pending behavior and no `getScript` fallback, instead of coupling to a single global catalog call count.

## Verification

- `corepack pnpm --dir apps/aevatar-console-web install --frozen-lockfile 2>&1 | tail -3` passed.
- `corepack pnpm --dir apps/aevatar-console-web build 2>&1 | tail -5` passed.
- `corepack pnpm --dir apps/aevatar-console-web test -- --selectProjects jsdom src/modules/studio/scripts/ScriptsWorkbenchPage.test.tsx --runInBand` passed: 18 tests.
- `bash tools/ci/test_stability_guards.sh` passed.
- `corepack pnpm --dir apps/aevatar-console-web test --runInBand 2>&1 | tail -10` passed: 106 suites, 686 tests.

## Command Notes

The console app declares `packageManager: pnpm@10.2.1` and has `pnpm-lock.yaml` rather than `yarn.lock`, so verification used pnpm, matching CI. The literal `pnpm test --run` form was checked and rejected by Jest because `--run` is not a valid Jest option for this project.
