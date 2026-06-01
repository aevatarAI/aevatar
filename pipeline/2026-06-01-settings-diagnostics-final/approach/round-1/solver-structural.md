# Approach solver - structural

role=solver_structural
thread=2026-06-01-settings-diagnostics-final
approach_round=1
verdict=propose

## CLAUDE clause violated
> Task request: "Add a third Settings section/tab named `Diagnostics` next to the existing `LLM` and `Account` sections."

> Host Policy: "Use existing frontend APIs and shared UI primitives. Do not add backend endpoints or change runtime contracts."

Current source evidence: `apps/aevatar-console-web/src/pages/settings/index.tsx` only models `type SettingsSection = "llm" | "account"`, `readSettingsSection` only accepts `account`, and `tabDefinitions` renders only `LLM` and `Account`; therefore the requested console readiness surface is absent.

## Recommended framing
把 Diagnostics 做成 Settings 页内部的第三个本地 section，而不是新增路由、后端接口或全局诊断服务；事实来源只取现有 auth session storage、`studioApi.getUserConfig`、`studioApi.getUserConfigModels`、runtime resolver 和 Umi 已注入到客户端的 env allowlist。为了让 UI 展示和 Markdown 复制共享同一份非密事实，应新增一个 settings-local 的纯 helper 生成 `SettingsDiagnosticsReport`，组件只负责用现有 `AevatarPanel`/`SummaryField`/`SummaryMetric` 渲染和触发 clipboard，这样秘密过滤、缺失值格式化、错误摘要和 URL tab 状态都可在本层测试，不需要架构例外。

## Concrete plan
- New abstractions: `SettingsDiagnosticsReport` local helper in `apps/aevatar-console-web/src/pages/settings/diagnosticsReport.ts`; shape is `buildSettingsDiagnosticsReport(input) -> { panels, markdown }`, where panels are typed safe display rows and markdown is the support bundle. This is page-local, not a shared platform abstraction.
- Files:
  - `apps/aevatar-console-web/src/pages/settings/diagnosticsReport.ts`: add pure helpers for safe value formatting, auth/session classification, provider readiness summary, runtime/env summary, API loading/error summary, and Markdown support bundle generation.
  - `apps/aevatar-console-web/src/pages/settings/diagnosticsContent.tsx`: add dense Ant Design Pro-compatible panels using `AevatarPanel`, `SummaryField`, `SummaryMetric`, compact mono text, and a `Copy diagnostics` button with AntD success/failure feedback.
  - `apps/aevatar-console-web/src/pages/settings/index.tsx`: extend `SettingsSection` to `llm | account | diagnostics`, accept `?section=diagnostics`, add the third tab/ref/href branch, preserve existing keyboard tab rail behavior, and pass existing query/session/env facts into `DiagnosticsSettingsContent`.
  - `apps/aevatar-console-web/src/pages/settings/index.test.tsx`: extend focused Settings tests for diagnostics navigation, session states, readiness summary, sanitized copy bundle, and existing LLM/Account behavior.
- LOC delta estimate: about `+520/-20` across 4 files. Rough split: `diagnosticsReport.ts` +160, `diagnosticsContent.tsx` +190, `index.tsx` +60/-10, `index.test.tsx` +110/-10.
- Tests to add/modify:
  - `renders Diagnostics tab and preserves ?section=diagnostics`: click tab and direct-load URL both select the Diagnostics panel.
  - `keeps keyboard navigation across three Settings tabs`: Arrow/Home/End still move focus and URL state through LLM, Account, Diagnostics.
  - `shows auth session present and missing states`: persisted auth session reports signed in, expiry, token type; cleared storage reports missing session/Unavailable.
  - `summarizes provider/model readiness`: mocked `getUserConfig` and `getUserConfigModels` produce effective route, default model, ready provider count, gateway URL, runtime mode/base URL.
  - `copies sanitized diagnostics markdown`: mock `navigator.clipboard.writeText`, persist access token/refresh token/id token/api-key-like fixture values, click Copy diagnostics, assert copied text contains safe fields and excludes token/secret values and bearer strings.
  - Keep existing LLM/Account tests passing; adjust only selectors made ambiguous by the new Diagnostics text.
- Runtime cost: no new backend calls because the page already issues the two React Query requests; Diagnostics reuses their status/data. Extra browser work is one localStorage read and local pure report build on render, O(provider count + env allowlist size). Clipboard runs only on click. Verification adds no daemons; `pnpm --dir apps/aevatar-console-web test --runInBand settings` should add roughly 5-7 jsdom cases, then `pnpm --dir apps/aevatar-console-web tsc`.

## Interface contract
- helper_name: `buildSettingsDiagnosticsReport`
- callers: `DiagnosticsSettingsContent` panel rendering consumes the typed rows; `handleCopyDiagnostics` consumes the same helper's `markdown` string for clipboard. Focused tests may import the helper only to assert sanitization edge cases, but UI and copy are the two production callers.
- fact contract: input facts are `readStoredAuthSession()` plus `hasActiveAccessToken()`, `StudioUserConfig` from `studioApi.getUserConfig`, `StudioUserConfigModelsResponse` from `studioApi.getUserConfigModels`, React Query loading/error states summarized with `describeError`, runtime facts via `normalizeStudioUserConfigRuntimeMode`/`resolveStudioUserConfigRuntimeBaseUrl`, and a hard-coded frontend env allowlist already exposed in `config/config.ts` (`AEVATAR_CONSOLE_PUBLIC_PATH`, `AEVATAR_CONSOLE_TEAM_FIRST_ENABLED`, `NYXID_BASE_URL`, `ORNN_BASE_URL`, plus auth configured/unavailable status without copying token/client-secret-like values). Output facts are display rows and a Markdown clipboard artifact containing only allowlisted non-secret values; no lock, commit, backend state, or persisted diagnostic fact is created.

## Failure/recovery
- failure_class: none
- recovery: no recovery needed beyond current local fact sources; failures are surfaced as Diagnostics API/error rows and test/tsc failures. Clipboard write rejection shows AntD failure feedback and does not mutate app state.

## Risks
- The helper may look heavier than inline JSX for a single tab. The cost is justified by the secret-exclusion contract and the need for identical UI/copy facts; keep it under `pages/settings` with no barrel export to prevent it becoming a generic diagnostics framework.
- API error messages may theoretically contain sensitive backend text. The bundle should not serialize raw responses; it should use `describeError` plus a final local redaction pass for key names/patterns such as `accessToken`, `refreshToken`, `idToken`, `apiKey`, `authorization`, and `Bearer ...`.
- Exposed frontend env URLs can reveal deployment endpoints, but they are already compiled into the client. Keep the allowlist explicit and show `Configured` instead of raw value for any field that is identity-like but not required for operator readiness.

## Escalation triggers
- none

## Reasoning trace
Checked `apps/aevatar-console-web/src/pages/settings/index.tsx`: existing URL-backed tab rail, ARIA tab keyboard handling, LLM query calls, provider readiness helpers, runtime resolver usage, and two-section typing are local and extendable. Checked `accountContent.tsx` and `shared/auth/session.ts`: session storage already exposes expiry/token type facts; use `readStoredAuthSession` and `hasActiveAccessToken` to distinguish missing/expired without copying token values. Checked `shared/studio/api.ts`, `shared/studio/models.ts`, and `userConfigRuntime.ts`: requested LLM/runtime facts are already available from existing API responses and helpers. Checked `config/config.ts`: only selected `process.env.*` values are client-exposed through Umi `define`; Diagnostics should use an explicit safe allowlist. Checked `index.test.tsx`: tests already mock the two required Studio APIs and auth session, so no new backend fixture is needed. Rejected adding backend APIs, global diagnostics stores, a new Settings route page, snapshot tests, or a shared redaction package outside Settings because none are required by current callers.

⟦AI:FKST⟧
