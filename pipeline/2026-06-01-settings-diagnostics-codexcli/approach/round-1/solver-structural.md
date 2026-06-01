# Approach solver - structural

role=solver_structural
thread=2026-06-01-settings-diagnostics-codexcli
approach_round=1
verdict=propose

## CLAUDE clause violated
> Task request: "Add a third Settings section/tab named `Diagnostics` next to the existing `LLM` and `Account` sections."
> Host Policy: "Use existing frontend APIs and shared UI primitives. Do not add backend endpoints or change runtime contracts."

There is no backend or architecture-layer violation to fix. The current Settings surface only models `SettingsSection = "llm" | "account"` and its tab definitions contain only `LLM` and `Account`, so the requested operator diagnostics capability is absent from the frontend while all required facts already exist in the app layer.

## Recommended framing
把 Diagnostics 做成 `apps/aevatar-console-web/src/pages/settings` 内部的只读诊断视图，而不是新增后端接口或共享全局诊断框架：Settings 现有页面已经统一拉取 `studioApi.getUserConfig` 与 `studioApi.getUserConfigModels`，Account 已经通过 `loadRestorableAuthSession` 读取本地会话，runtime base URL 也已有 `userConfigRuntime` 解析函数；新增一个 settings-local 的强类型 view-model/bundle helper，把这些既有事实整理为可渲染面板与可复制 Markdown，并通过构造入参避免 token、refresh token、API key、provider secret 进入复制路径。这样结构上保留 Ant Design Pro Settings 壳、URL tab 状态和键盘 tab rail，不需要例外或跨层扩展。

## Concrete plan
- New abstractions: `buildSettingsDiagnosticsViewModel(input)` and `buildSettingsDiagnosticsMarkdown(viewModel)` as settings-local pure helpers in `apps/aevatar-console-web/src/pages/settings/diagnosticsModel.ts`; no shared package export, no backend/API surface.
- Files:
  - `apps/aevatar-console-web/src/pages/settings/index.tsx`: extend `SettingsSection` to include `diagnostics`, parse/build `?section=diagnostics`, add the tab definition/ref entry, render `DiagnosticsSettingsContent`, and keep LLM save buttons only on the LLM tab.
  - `apps/aevatar-console-web/src/pages/settings/diagnosticsModel.ts`: derive sanitized diagnostics facts from auth session, user config, model config, query statuses/errors, runtime helpers, and client-exposed env flags; build the Markdown support bundle from sanitized fields only.
  - `apps/aevatar-console-web/src/pages/settings/diagnosticsContent.tsx`: render dense read-only `AevatarPanel`/`SummaryField`/`SummaryMetric` panels and own the clipboard copy action with Ant Design success/error feedback.
  - `apps/aevatar-console-web/src/pages/settings/index.test.tsx`: add focused Diagnostics tests and adjust existing tab assertions for the third tab.
- LOC delta estimate: `+430/-18` total; roughly `index.tsx +55/-8`, `diagnosticsModel.ts +150`, `diagnosticsContent.tsx +180`, `index.test.tsx +45/-10`.
- Tests to add/modify:
  - Diagnostics tab navigation sets `window.location.search` to `?section=diagnostics`; ArrowRight/ArrowLeft/Home/End still move focus and route across all 3 tabs.
  - Auth present state renders signed-in/restorable status, token expiry label, and token type without rendering token values.
  - Missing auth state renders `Missing session` and `Unavailable`/`n/a` for absent auth fields.
  - Provider/model readiness renders effective route, default model, ready provider count, unavailable provider count, and gateway URL from `getUserConfigModels`.
  - Copy diagnostics calls `navigator.clipboard.writeText` with Markdown that includes non-secret config facts and excludes fixture strings such as `access-token-secret`, `refresh-token-secret`, `api-key-secret`, and `Bearer access-token-secret`.
  - Existing LLM default render, route picker, save, and Account tab behavior continue to pass with the added third tab.
- Runtime cost: no new processes, no new network calls, no file scans, no locks, no logs. Production runtime adds one localStorage read through existing auth session helper during Diagnostics render, pure in-memory derivation over the existing provider array, and one clipboard write only when the operator clicks Copy. Verification cost is the requested two commands: one focused Jest run and one TypeScript check.

## Interface contract
- helper_name: `buildSettingsDiagnosticsViewModel`
- callers: `DiagnosticsSettingsContent` uses it to render panels; `buildSettingsDiagnosticsMarkdown` consumes the same view model for the copy bundle, so UI and copied facts share one sanitized fact record.
- fact contract: inputs are already-client-visible facts: `NyxIDAuthSession | null` from `loadRestorableAuthSession`, `StudioUserConfig | undefined` from `studioApi.getUserConfig`, `StudioUserConfigModelsResponse | undefined` from `studioApi.getUserConfigModels`, React Query loading/error states summarized through `describeError`, runtime base/mode from `userConfigRuntime`, and explicitly defined client env labels from `process.env.*`. Output is a typed diagnostics view model plus a Markdown string containing only labels, counts, URLs, mode strings, expiry timestamps, and boolean availability; token/access-token/refresh-token/API-key fields are not accepted as bundle inputs.

## Failure/recovery
- failure_class: none
- recovery: API failures remain local UI facts from React Query and render as Diagnostics error summaries; clipboard write failures show Ant Design error feedback. No scanner, re-dispatch, external production check, or manual host review is required for the proposed scope.

## Risks
- The helper adds more structure than an inline JSX-only implementation, but it buys an auditable sanitization boundary for the copy bundle and keeps six-month maintenance out of the large `index.tsx` body.
- Client-exposed env flags must be curated, not blindly enumerated. Narrow to operational non-secret values already defined in `config/config.ts`, and represent sensitive-looking names such as `NYXID_CLIENT_ID` as configured/not configured or omit them from the copied bundle if there is any doubt.
- The copied Markdown may drift from visible panels if future edits bypass the view model. Keep both render and bundle paths fed by the same typed diagnostics record and add a test that copies from a fixture containing secret-like token values.

## Escalation triggers
- none

## Reasoning trace
Checked current Settings files: `index.tsx` has only `llm` and `account` section keys, two tab definitions, URL-backed `readSettingsSection`, and existing keyboard handling over `tabDefinitions`; adding a third key fits the existing mechanism. The page already runs `studioApi.getUserConfig()` and `studioApi.getUserConfigModels()`, so Diagnostics should reuse those query objects instead of adding APIs. `accountContent.tsx` uses `loadRestorableAuthSession()` and displays token expiry/type without raw tokens, which is the correct local auth fact source. `userConfigRuntime.ts` already normalizes runtime mode and resolves local/remote base URLs. `config/config.ts` exposes public path and selected client env flags through Umi `define`, so Diagnostics should read a curated allowlist rather than scanning environment keys. Rejected alternatives: adding backend health endpoints, introducing a shared diagnostics framework, enumerating all `process.env`, or copying serialized user config/session objects, because each would widen surface area or risk leaking secrets.

⟦AI:FKST⟧
