# Approach solver - minimal

role=solver_minimal
thread=2026-06-01-settings-diagnostics-codexcli
approach_round=1
verdict=propose

## Recommended framing
在现有 `SettingsPage` 内增加第三个 `Diagnostics` tab，并复用页面已经加载的 `studioApi.getUserConfig`、`studioApi.getUserConfigModels`、`loadRestorableAuthSession`、runtime 解析 helper 与已暴露到客户端的 env 值来渲染只读诊断面板；这是最小边界，因为需求只是前端可观测性，不需要新增后端 API、共享模型、SDK 契约或跨目录抽象，且现有 tab rail、Ant Design Pro shell、Settings panel primitives 已能承载 URL 状态、键盘导航和紧凑运营视图。

## Concrete plan
- Files: `apps/aevatar-console-web/src/pages/settings/index.tsx`：将 `SettingsSection` 扩展为 `llm | account | diagnostics`，让 `readSettingsSection`/`buildSettingsHref` 支持 `?section=diagnostics`，在现有 tab definitions/ref map/tabpanel 渲染里加入 `Diagnostics`；新增只读 diagnostics section，展示 auth session 状态、access token expiry 状态与 token type、LLM effective route/default model/ready provider count/gateway URL、runtime mode/resolved base URL/local-or-remote label、public path 与客户端已 define 的 env/config flags、两个 user-config API query 的 loading/error 摘要；新增 `Copy diagnostics` 按钮，用 whitelist 生成 Markdown bundle 并调用 `navigator.clipboard.writeText`，只包含非 secret 字段。
- Files: `apps/aevatar-console-web/src/pages/settings/index.test.tsx`：扩展现有 focused Settings tests，mock clipboard，覆盖 diagnostics tab URL state、auth session present/missing、provider/model readiness summary、copy bundle 不含 access/refresh/id token 或 secret-looking values，并保留现有 LLM/Account 行为测试。
- LOC delta estimate: source `+220/-20`，tests `+130/-5`，total approximately `+350/-25` across 2 files.
- Tests to add/modify: `apps/aevatar-console-web/src/pages/settings/index.test.tsx` only; run `pnpm --dir apps/aevatar-console-web test --runInBand settings` and `pnpm --dir apps/aevatar-console-web tsc`.
- Rule exception: none.
- Migration path: no migration needed.

## Failure/recovery
- failure_class: none
- recovery: no recovery needed; local focused Settings test run plus frontend typecheck are sufficient current fact sources.

## Risks
- The Settings page is already large, so adding the diagnostics panel inline increases one file further; this is still smaller and more auditable than introducing a new shared diagnostics abstraction for a single page.
- Client-exposed env values are build-time substitutions, so the UI should label missing values as `Unavailable`/`n/a` and avoid implying live backend state beyond the two existing API query results.
- The copy bundle must remain whitelist-based; never stringify auth session, provider objects, localStorage, query data, or errors wholesale.

## Escalation triggers
- none

## Reasoning trace
Checked `apps/aevatar-console-web/src/pages/settings/index.tsx`: Settings already owns `getUserConfig` and `getUserConfigModels`, computes effective route, provider readiness, model groups, runtime mode/base URL, and implements URL-backed keyboard-accessible tab rail for `LLM` and `Account`. Checked `accountContent.tsx` and `shared.tsx`: existing session presentation and `AevatarPanel`/summary primitives can be reused without changing visual language. Checked `shared/auth/session.ts`: `loadRestorableAuthSession` and `hasActiveAccessToken` expose session presence/expiry/type without needing to read or render token values. Checked `shared/studio/models.ts`, `shared/studio/api.ts`, and `shared/studio/userConfigRuntime.ts`: user config/model responses provide required non-secret provider/model/runtime fields and existing runtime fallback defaults. Checked `config/config.ts`, `shared/auth/config.ts`, and `shared/studio/ornnConfig.ts`: public path and relevant client-exposed env/config values are already defined client-side. Rejected backend API changes, new shared contracts, docs/architecture edits, and cross-app diagnostics utilities as outside the minimal frontend-only boundary.

⟦AI:FKST⟧
