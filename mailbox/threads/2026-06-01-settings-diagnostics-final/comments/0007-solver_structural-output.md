# Approach solver - structural

role=solver_structural
thread=2026-06-01-settings-diagnostics-final
approach_round=2
verdict=propose

## CLAUDE clause violated
> Add a third Settings section/tab named `Diagnostics` next to the existing `LLM` and `Account` sections.
> Add a `Copy diagnostics` action that copies a Markdown support bundle to the clipboard. The bundle should include only non-secret values. Never copy access tokens, refresh tokens, API keys, or provider secrets.
> API/provider loading state and error summaries using existing `studioApi.getUserConfig` and `studioApi.getUserConfigModels`; do not add backend APIs.

Current repository fact: `apps/aevatar-console-web/src/pages/settings/index.tsx` defines `SettingsSection = "llm" | "account"`, `readSettingsSection` only recognizes `account`, and `tabDefinitions` renders only `LLM` and `Account`; no operator diagnostics section or support bundle exists.

## Recommended framing
在 `apps/aevatar-console-web/src/pages/settings` 内新增本地 `diagnosticsReport.ts` 与 `diagnosticsContent.tsx`，让 `index.tsx` 只扩展 `diagnostics` tab、URL 状态和现有查询事实传递；`diagnosticsReport.ts` 负责把 auth/user-config/models/env/query 状态收敛成一个强 allowlist 的 `SettingsDiagnosticsReport`，并由同一份 report 同时驱动面板展示与 Markdown copy，避免 UI 与复制内容双轨拼装，也避免把 `StudioUserConfig`、auth session 或 provider 对象直接 `JSON.stringify` 到剪贴板。该结构复用现有 Account 已拆 `accountContent.tsx` 的页面边界，不新增共享抽象、不改后端、不改 runtime contract，代价是多一个 Settings 本地纯 helper 文件，但六个月后审计“哪些字段会被复制”可以直接看一个 allowlist。

## Concrete plan
- New abstractions: `SettingsDiagnosticsReport` local type in `apps/aevatar-console-web/src/pages/settings/diagnosticsReport.ts`; shape is `{ generatedAtIso, sections: Array<{ title, rows: Array<{ label, value, copyValue?, tone? }> }>, markdown }` or equivalent. Keep it Settings-local, exported only for the Diagnostics component and optional focused tests. Add `buildSettingsDiagnosticsReport(input)` and `formatSettingsDiagnosticsMarkdown(report)`; do not move it to `shared/`.
- Files:
  - `apps/aevatar-console-web/src/pages/settings/index.tsx`: add `diagnostics` to `SettingsSection`, `readSettingsSection`, `buildSettingsHref`, `tabDefinitions`, tab refs, content description, and tabpanel selection; pass existing `userConfigQuery`, `userConfigModelsQuery`, `draft`, effective route/model/runtime facts to Diagnostics instead of issuing new queries.
  - `apps/aevatar-console-web/src/pages/settings/diagnosticsReport.ts`: new pure report builder and Markdown formatter with explicit non-secret field allowlist. Inputs are `loadRestorableAuthSession()` result or null, resolved runtime facts, `StudioUserConfig`, `StudioUserConfigModelsResponse`, query loading/error summaries, and client-exposed env values only.
  - `apps/aevatar-console-web/src/pages/settings/diagnosticsContent.tsx`: new dense Ant Design/AevatarPanel UI using existing `SummaryField`, `SummaryMetric`, `buildSettingsPanelStyle`, `describeError`, `getNyxIDRuntimeConfig`, `getOrnnRuntimeConfig`, and `CONSOLE_FEATURES`; add `Copy diagnostics` button that writes `report.markdown` to `navigator.clipboard.writeText` and shows Ant Design success/failure feedback.
  - `apps/aevatar-console-web/src/pages/settings/index.test.tsx`: extend focused Settings tests for diagnostics navigation, auth states, readiness summary, clipboard exclusion, and existing tab behavior.
- LOC delta estimate: `+440/-12` total: `index.tsx +55/-8`, `diagnosticsReport.ts +145`, `diagnosticsContent.tsx +190`, `index.test.tsx +50/-4`. If copy feedback mocking requires local helper assertions, add up to `+35` more test LOC and still stay within Settings.
- Tests to add/modify:
  - Add diagnostics tab navigation test: click `Diagnostics`, assert `window.location.search === "?section=diagnostics"`, reload/render with that query opens Diagnostics, and ArrowLeft/ArrowRight/Home/End still move focus through the three-tab rail.
  - Add auth-present diagnostics test: persist a session, render Diagnostics, assert signed-in status, formatted expiry/token type, and user identifier summary; assert no access token or refresh token text appears.
  - Add auth-missing diagnostics test: clear storage, render Diagnostics, assert `Missing session` plus `Unavailable`/`n/a` values rather than empty cells.
  - Add provider/model readiness test: mock mixed ready/unavailable providers and configured model/route; assert effective route, default model, ready provider count, gateway URL, runtime mode label, and resolved runtime base URL.
  - Add copy diagnostics test: mock `navigator.clipboard.writeText`, click `Copy diagnostics`, assert Markdown contains allowlisted values and excludes `accessToken`, `refreshToken`, `apiKey`, `Bearer token`, and any provider secret fixture strings; assert success and a rejected clipboard promise produces failure feedback.
  - Keep existing LLM and Account tests intact, updating role queries only where the third tab changes tab order or accessible names.
- Runtime cost: no extra backend calls beyond the two existing React Query calls already made by Settings; no extra processes, file scans, git commands, locks, or log volume. Runtime work is one memoized O(provider count + model count) report build and one clipboard write of an expected sub-10KB Markdown string.

## Interface contract
- helper_name: `buildSettingsDiagnosticsReport(input)` and `formatSettingsDiagnosticsMarkdown(report)` in `diagnosticsReport.ts`.
- callers: `diagnosticsContent.tsx` panel rendering consumes `report.sections`; `diagnosticsContent.tsx` copy handler consumes `report.markdown` or `formatSettingsDiagnosticsMarkdown(report)`. `index.test.tsx` verifies both callers through rendered UI and clipboard output; direct helper import is optional only if the clipboard secret-exclusion assertion becomes too brittle through the DOM.
- fact contract: input facts are current browser auth session from `loadRestorableAuthSession`, existing Settings query facts from `studioApi.getUserConfig` and `studioApi.getUserConfigModels`, existing runtime helpers `normalizeStudioUserConfigRuntimeMode`/`resolveStudioUserConfigRuntimeBaseUrl`, and client-exposed env config from `getNyxIDRuntimeConfig`, `getOrnnRuntimeConfig`, `CONSOLE_FEATURES`, and `process.env.AEVATAR_CONSOLE_PUBLIC_PATH`. Output fact is an in-memory report and Markdown clipboard artifact; it must never include token values, refresh tokens, id tokens, API keys, provider secrets, or raw serialized auth/config/provider objects.

## Failure/recovery
- failure_class: conformance-fail
- recovery: local scanner is the focused Settings test suite plus `tsc`; failures recover by adjusting the Settings-local allowlist/report mapping or tab composition and rerunning `pnpm --dir apps/aevatar-console-web test --runInBand settings` and `pnpm --dir apps/aevatar-console-web tsc`. No mailbox, backend, production system, or external fact source is needed.

## Risks
- This is heavier than the two-file inline framing by roughly one new pure helper and one component file; the trade-off buys an auditable non-secret copy contract and keeps the already 1424-line `index.tsx` from absorbing another operational view.
- It may be over-built if Diagnostics remains a tiny static panel. Narrow by keeping `diagnosticsReport.ts` non-shared, exporting only the report builder/formatter, avoiding reducers/classes/context providers, and deleting any formatter branch not exercised by a requested panel or copy field.

## Escalation triggers
- none

## Reasoning trace
Checked `apps/aevatar-console-web/src/pages/settings/index.tsx`, `index.test.tsx`, `shared.tsx`, and `accountContent.tsx`; Settings already has a local component split for Account and a large mixed LLM page, while tab keyboard behavior is centralized through `tabDefinitions`. Checked `shared/studio/models.ts`, `shared/studio/api.ts`, and `shared/studio/userConfigRuntime.ts`; the required LLM/runtime facts already exist through `getUserConfig`, `getUserConfigModels`, and runtime helpers. Checked `shared/auth/session.ts`, `shared/auth/config.ts`, `shared/studio/ornnConfig.ts`, `shared/config/consoleFeatures.ts`, and `config/config.ts`; auth token values are present in local storage APIs but must be summarized only by status/expiry/type, and client-exposed env flags are a finite allowlist. Rejected adding backend APIs, shared config scanners, docs, or protected-path changes. Rejected fully inline implementation because the copy allowlist would be interleaved with JSX and LLM editing state, increasing the chance of accidental raw-object copying or future drift between UI and support bundle.

⟦AI:FKST⟧
