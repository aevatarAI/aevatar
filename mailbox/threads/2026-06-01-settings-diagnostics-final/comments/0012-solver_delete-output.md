# Approach solver - delete

role=solver_delete
thread=2026-06-01-settings-diagnostics-final
approach_round=3
verdict=propose

## Classification
d - 需求本身是真实的 Settings 前端可观测性缺口，但新增 `diagnosticsReport.ts` / `diagnosticsContent.tsx` / exported report contract 会把一次页面内诊断视图做成可复用子系统；当前事实支持删除这层抽象，只保留 Settings-local inline rendering 与 allowlist Markdown formatter。

## Recommended action
建议收敛为只编辑 `apps/aevatar-console-web/src/pages/settings/index.tsx` 与 `apps/aevatar-console-web/src/pages/settings/index.test.tsx`：Diagnostics 应作为现有 Settings 页面第三个 section 存在，复用当前页面已经加载的 `getUserConfig`、`getUserConfigModels`、runtime 派生值、tab rail、Ant Design panel/summary primitives，并在同文件内增加非导出的 allowlist Markdown builder；不要新增 backend API、不要新增 Settings-local diagnostics 模块、不要导出 `SettingsDiagnosticsReport` 之类的本地契约，避免把一次支持包生成逻辑变成长期维护 surface。

## Concrete plan
- Files to delete: none in current repo; if a candidate adds `apps/aevatar-console-web/src/pages/settings/diagnosticsReport.ts` or `apps/aevatar-console-web/src/pages/settings/diagnosticsContent.tsx`, delete those files and inline the small helpers into `index.tsx`.
- Files to edit: `apps/aevatar-console-web/src/pages/settings/index.tsx`; `apps/aevatar-console-web/src/pages/settings/index.test.tsx`.
- Caller migrations: none; `SettingsPage` and `buildSettingsRouteSelectOptions` are only consumed by the focused Settings test in current search results, and no external consumer needs a diagnostics report API.
- Tests to delete/update/add: update existing Settings tests for tab navigation/URL state, auth present/missing, readiness summary, copy bundle secret exclusion, and existing LLM/Account behavior; add no separate helper-module tests.
- LOC delta estimate: +230/-10.
- Migration path: add `diagnostics` to the existing Settings section union/tab definitions and render/copy logic in `index.tsx`; no compatibility shim.

## Reverse-evidence
- `apps/aevatar-console-web/src/pages/settings/index.tsx:72` currently defines the section union as only `"llm" | "account"`, and `apps/aevatar-console-web/src/pages/settings/index.tsx:184` to `apps/aevatar-console-web/src/pages/settings/index.tsx:197` already centralize URL section parsing and href creation, so Diagnostics fits the existing section switch.
- `apps/aevatar-console-web/src/pages/settings/index.tsx:561` to `apps/aevatar-console-web/src/pages/settings/index.tsx:568` already load exactly the requested API facts through `studioApi.getUserConfig` and `studioApi.getUserConfigModels`; no backend endpoint or new data source is needed.
- `apps/aevatar-console-web/src/pages/settings/index.tsx:617` to `apps/aevatar-console-web/src/pages/settings/index.tsx:729` already derives providers, ready provider count, effective route, runtime mode label, and runtime URL inputs used by the requested diagnostics.
- `apps/aevatar-console-web/src/pages/settings/index.tsx:856` to `apps/aevatar-console-web/src/pages/settings/index.tsx:875` already builds a compact technical preview row list for route/model/runtime values, making a separate report data contract duplicative.
- `apps/aevatar-console-web/src/pages/settings/index.tsx:965` to `apps/aevatar-console-web/src/pages/settings/index.tsx:1005` defines the tab array and keyboard behavior, and `apps/aevatar-console-web/src/pages/settings/index.tsx:1386` to `apps/aevatar-console-web/src/pages/settings/index.tsx:1417` renders the accessible tablist/panel; adding a third key here preserves the existing keyboard model.
- `apps/aevatar-console-web/src/pages/settings/shared.tsx:33` to `apps/aevatar-console-web/src/pages/settings/shared.tsx:88` and `apps/aevatar-console-web/src/pages/settings/shared.tsx:109` to `apps/aevatar-console-web/src/pages/settings/shared.tsx:146` already provide Settings panel/switch/summary primitives, so a new diagnostics content component would mostly wrap existing local UI patterns.
- `apps/aevatar-console-web/src/pages/settings/accountContent.tsx:37` to `apps/aevatar-console-web/src/pages/settings/accountContent.tsx:40` already reads the restorable auth session, and `apps/aevatar-console-web/src/pages/settings/accountContent.tsx:142` to `apps/aevatar-console-web/src/pages/settings/accountContent.tsx:160` shows token expiry/type/refresh availability; Diagnostics can use the same auth session helper without new auth surface.
- `apps/aevatar-console-web/src/shared/auth/session.ts:4` to `apps/aevatar-console-web/src/shared/auth/session.ts:11` proves session tokens contain `accessToken`, `refreshToken`, and `idToken`; therefore the copy bundle must be built from explicit non-secret fields instead of serializing session objects.
- `apps/aevatar-console-web/config/config.ts:150` to `apps/aevatar-console-web/config/config.ts:164` lists frontend env values exposed to the client, which is sufficient evidence for a narrow allowlist of public path and configured client-visible flags.
- `apps/aevatar-console-web/src/shared/studio/models.ts:778` to `apps/aevatar-console-web/src/shared/studio/models.ts:800` defines the `StudioUserConfig` and `StudioUserConfigModelsResponse` fields available to Diagnostics; they cover runtime mode/base URLs, default model, providers, gateway URL, and supported models without adding contracts.
- `apps/aevatar-console-web/src/shared/studio/models.ts:760` to `apps/aevatar-console-web/src/shared/studio/models.ts:766` contains API key fields in a different settings model, which reinforces the need for local allowlist formatting rather than generic object dumping.
- `apps/aevatar-console-web/src/shared/studio/api.ts:2268` to `apps/aevatar-console-web/src/shared/studio/api.ts:2290` confirms the existing API methods and paths for user config and model readiness; adding backend APIs would violate the task boundary.

## Failure/recovery
- failure_class: none
- recovery: no recovery needed; conformance is covered by the focused Settings tests and the requested `pnpm --dir apps/aevatar-console-web test --runInBand settings` plus `pnpm --dir apps/aevatar-console-web tsc` verification.

## Risks
- Keeping the helpers inline can make `index.tsx` larger; cap the new code to a rendering block plus small non-exported `formatUnavailable`, `formatDiagnosticsMarkdown`, and `copyDiagnostics` helpers, and only split later if another real caller appears.
- The main safety risk is copying too much. The Markdown bundle must enumerate fields one by one and never stringify auth/session/config/provider objects wholesale.
- `navigator.clipboard` can be unavailable or reject; the action must report failure via existing Ant Design feedback instead of silently succeeding.
- Diagnostics should show loading/error summaries without changing query semantics; do not trigger extra priming, save, runtime test, or provider refresh behavior from this tab.

## Escalation triggers
- none

## Reasoning trace
Checked the current Settings page, shared Settings primitives, account session UI, auth session storage contract, studio API/model contracts, exposed frontend env config, and current Settings tests. The requested capability should exist because operators currently have to infer readiness from the LLM and Account tabs separately, but the data is already present inside `SettingsPage`. Rejected adding `diagnosticsReport.ts` because there is one caller, no reusable domain contract, no persistence boundary, no backend/API contract, and the safest secret-exclusion strategy is a local allowlist builder close to the UI facts it displays. Rejected "do nothing" because the current page has only LLM/Account tabs and no copyable support bundle.

⟦AI:FKST⟧
