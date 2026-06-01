# Approach solver - structural

role=solver_structural
thread=2026-06-01-settings-diagnostics-final
approach_round=3
verdict=propose

## CLAUDE clause violated
> Add a third Settings section/tab named `Diagnostics` next to the existing `LLM` and `Account` sections.
> Add a `Copy diagnostics` action that copies a Markdown support bundle to the clipboard. The bundle should include only non-secret values.
> Use existing frontend APIs and shared UI primitives. Do not add backend endpoints or change runtime contracts.

Current `apps/aevatar-console-web/src/pages/settings/index.tsx` only models `type SettingsSection = "llm" | "account"` and `tabDefinitions` contains only `LLM` and `Account`, so the requested operator diagnostics surface and copy bundle do not exist.

## Recommended framing
本轮应把 Diagnostics 作为 Settings 页面内的第三个本地 section 实现，继续复用现有 `studioApi.getUserConfig` / `studioApi.getUserConfigModels` 查询、`loadRestorableAuthSession`、runtime resolver 与 Settings shared UI，而不是新增后端或跨层契约；新增 `diagnosticsReport.ts`/`diagnosticsContent.tsx` 不是严格必需，非密钥 copy 契约可以由 `index.tsx` 内靠近 Diagnostics section 的私有 allowlist 行构造和 Markdown 格式化完成，并通过 clipboard 行为测试审计，避免为了单一页面功能导出新的本地 report surface。

## Concrete plan
- New abstractions: none. Add no exported report type, SDK surface, backend API, route contract, or shared Settings abstraction.
- Files:
  - `apps/aevatar-console-web/src/pages/settings/index.tsx`: add `diagnostics` to `SettingsSection`, `readSettingsSection`, `buildSettingsHref`, `handleSectionChange`, `tabDefinitions`, tab refs, header copy action, and a dense Diagnostics tab body using existing `AevatarPanel`, `SummaryField`, `SummaryMetric`, `FieldMetaPill`, and existing query/runtime helpers.
  - `apps/aevatar-console-web/src/pages/settings/index.test.tsx`: extend focused Settings tests for Diagnostics navigation, auth status variants, readiness summaries, clipboard Markdown redaction, and existing LLM/Account behavior.
- LOC delta estimate: `+330/-25` total; about `+210/-20` in `index.tsx` and `+120/-5` in `index.test.tsx`.
- Tests to add/modify:
  - Navigate to Diagnostics by click and by initial `?section=diagnostics`; assert URL state remains `?section=diagnostics` and tabpanel is labelled by `diagnostics-tab`.
  - Exercise keyboard movement on the custom tab rail with three tabs, including ArrowRight/ArrowLeft/Home/End preserving focus target and URL.
  - With `persistAuthSession`, assert Diagnostics shows signed-in status, token expiry, and token type, but not access token, refresh token, or ID token values.
  - With no restorable session, assert Diagnostics shows `Missing session` and missing fields as `Unavailable` or `n/a`.
  - With model/provider fixtures, assert effective route, default model, ready provider count, gateway URL, runtime mode/base URL, frontend public path, and exposed feature flags.
  - Mock `navigator.clipboard.writeText`; assert copied Markdown includes only allowlisted non-secret fields and excludes token strings, `refreshToken`, `accessToken`, `idToken`, `apiKey`, provider secrets, and bearer values beyond token type.
  - Mock `getUserConfig` / `getUserConfigModels` loading and rejection states; assert loading/error summaries render without devtools.
  - Keep existing default LLM tab, Account tab, route picker, model picker, and save behavior tests passing.
- Runtime cost: 0 extra processes, 0 git commands, 0 file scans, 0 locks, and 0 extra API calls beyond the two existing Settings queries. Render cost is O(provider count + exposed env flag count). Copy action adds one `navigator.clipboard.writeText` call and one Ant Design message on user click.

## Interface contract
- helper_name: none as exported abstraction. Private local functions may format display values and build an allowlisted Markdown string inside `index.tsx`, but they are implementation details rather than a new Settings interface.
- callers: no helper is added as a shared or exported surface. The only runtime caller of the private Markdown formatter is the `Copy diagnostics` click handler; tests verify it through the public Settings UI and clipboard mock instead of importing a page-local test seam.
- fact contract: input facts are existing React Query results from `studioApi.getUserConfig` and `studioApi.getUserConfigModels`, `loadRestorableAuthSession`, `resolveStudioUserConfigRuntimeBaseUrl`, `normalizeStudioUserConfigRuntimeMode`, `formatStudioUserConfigRuntimeModeLabel`, `CONSOLE_FEATURES`, and `process.env.AEVATAR_CONSOLE_PUBLIC_PATH` as already exposed by Umi config. Output facts are the URL section key, the rendered Diagnostics panels, Ant Design success/failure feedback, and the clipboard Markdown support bundle containing only allowlisted non-secret values.

## Failure/recovery
- failure_class: none
- recovery: no production recovery path is needed for this frontend-only feature. Unsafe copy content, broken URL state, or missing loading/error summaries are local conformance failures recovered by editing the same Settings files and rerunning `pnpm --dir apps/aevatar-console-web test --runInBand settings` plus `pnpm --dir apps/aevatar-console-web tsc`.

## Risks
- The two-file framing keeps surface area smallest but grows an already large `index.tsx` from 1,424 lines to roughly 1,610 lines; the mitigation is to keep Diagnostics rows compact, allowlisted, and visually isolated in one memoized section rather than scattering helpers through the file.
- This is less structurally tidy than a sibling `diagnosticsContent.tsx` like `accountContent.tsx`; extraction should only happen if implementation exceeds the estimate materially or duplicates copy/report logic, because a new exported `diagnosticsReport.ts` would be permanent surface with no second runtime caller.

## Escalation triggers
- none

## Reasoning trace
Checked `apps/aevatar-console-web/src/pages/settings/index.tsx`, `index.test.tsx`, `shared.tsx`, `accountContent.tsx`, `shared/studio/api.ts`, `shared/studio/models.ts`, `shared/studio/userConfigRuntime.ts`, `shared/auth/session.ts`, `shared/config/consoleFeatures.ts`, and `config/config.ts`. The page already loads the two required Settings API facts and already has custom ARIA tab keyboard handling, so Diagnostics can be expressed by adding a third section key and reusing current query state. `diagnosticsReport.ts` is rejected because the non-secret bundle has one runtime consumer and can be verified through clipboard behavior; exporting a report contract only for test import is not required. `diagnosticsContent.tsx` is acceptable if the implementation grows, but not required to satisfy the architecture or safety contract in this gate.

⟦AI:FKST⟧
