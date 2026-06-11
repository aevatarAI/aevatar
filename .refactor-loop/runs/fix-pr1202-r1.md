## 🤖 fix r1 report

PR 1202 round 1 rejects 已处理。

Applied count: 5

Files:
- `apps/aevatar-console-web/src/shared/asyncOperations/index.ts`
- `apps/aevatar-console-web/src/shared/asyncOperations/index.test.ts`
- `apps/aevatar-console-web/src/pages/studio/index.tsx`
- `apps/aevatar-console-web/src/modules/studio/scripts/ScriptsWorkbenchPage.tsx`

Changes:
- 补齐 `shared/asyncOperations` module-level Old/New self-doc。
- 补齐 Studio member binding helper 接入点 inline self-doc，并注明现有 page-local timeout 与 helper injectable scheduler 边界。
- 补齐 Scripts workbench save observation helper 接入点 inline self-doc，并注明现有 page-local timeout 与 helper injectable scheduler 边界。
- 新增 `probeAsyncOperation` retryable error exhaustion 直接测试。
- 新增 `probeAsyncOperation` pending observation exhaustion 直接测试。

New tests:
- `returns retryable probe errors after attempts are exhausted`
- `returns the latest pending observation when attempts are exhausted`

Validation:
- `corepack pnpm --dir apps/aevatar-console-web test --runInBand` PASS: 106 suites, 667 tests passed.
- `corepack pnpm --dir apps/aevatar-console-web tsc` PASS.
- `bash /Users/auric/aevatar/tools/ci/test_stability_guards.sh` PASS: polling waits constrained by allowlist; Python guard tests 6 passed.

Notes:
- 未改业务逻辑，只补注释与 deterministic test coverage。
- `prompts/_github-post-rules.md` 在当前 checkout 与 `/Users/auric/aevatar` 中未找到；本次按任务内显式 GitHub post 规则执行。

⟦AI:AUTO-LOOP⟧
