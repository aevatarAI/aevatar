# Approach solver - minimal

role=solver_minimal
thread=2026-06-01-settings-diagnostics-final
approach_round=1
verdict=propose

## Recommended framing
当前 `apps/aevatar-console-web/src/pages/settings/index.tsx` 的设置页只有 `llm | account` 两个 section，且页面内已经持有诊断所需的大部分事实：本地 auth session、`studioApi.getUserConfig`、`studioApi.getUserConfigModels`、LLM route/model 解析、runtime mode/base URL 解析；最小边界是在 Settings 前端内部增加第三个 `diagnostics` section，复用现有 query 结果和 Settings/AevatarPanel 样式，新增一个只输出非 secret 字段的 Markdown copy builder，并在同一个 focused test 文件覆盖导航、状态与脱敏，不新增后端 API、不改共享契约、不碰 host protected paths。

## Concrete plan
- Files: `apps/aevatar-console-web/src/pages/settings/index.tsx`：把 `SettingsSection` 扩展为 `llm | account | diagnostics`；新增 `diagnosticsTabKey`、URL 解析/生成支持 `?section=diagnostics`；tabDefinitions 增加 `Diagnostics`，复用现有 tablist button 与 Arrow/Home/End 键盘逻辑；新增本文件内的 Diagnostics render 分支，展示 auth session、LLM defaults、runtime mode、frontend env、API/provider loading/error summary，并加入 `Copy diagnostics` 按钮。
- Files: `apps/aevatar-console-web/src/pages/settings/index.test.tsx`：扩展 mock API 类型和测试数据；mock `navigator.clipboard.writeText`；新增 diagnostics tab URL、session present/missing、provider/model readiness、copy bundle 脱敏测试；保留并调整既有 LLM/Account 断言。
- LOC delta estimate: +260/-20。
- Tests to add/modify: modify `apps/aevatar-console-web/src/pages/settings/index.test.tsx` only; add 4-5 focused test cases and keep existing route/model/save/account behavior passing.
- Rule exception: none。
- Migration path: no migration needed。

## Failure/recovery
- failure_class: none
- recovery: no recovery needed; failures are locally recoverable through the focused Settings test command and TypeScript check.

## Risks
- Diagnostics must not accidentally serialize token-bearing objects. Keep copy bundle construction as an explicit whitelist of scalar display fields, not a spread/stringify of auth session, user config, provider objects, environment objects, or errors.
- `getUserConfigModels` provider `proxyUrl` and `gatewayUrl` are operational URLs and safe to show, but they may be long; UI should truncate visually while copying exact non-secret URL values.
- Frontend env flags are only those already exposed by `config/config.ts` define entries. Do not add new env exposure solely for diagnostics.
- Because Account currently reads `loadRestorableAuthSession()` locally, Diagnostics should do the same for browser-session facts rather than adding a new query dependency.

## Escalation triggers
- none

## Reasoning trace
Checked current repository facts: `apps/aevatar-console-web/src/pages/settings/index.tsx` defines `type SettingsSection = "llm" | "account"`, `readSettingsSection` only accepts account and otherwise defaults to llm, `tabDefinitions` contains only LLM and Account, and active panel rendering chooses only LLM vs Account. The same file already queries `studioApi.getUserConfig()` and `studioApi.getUserConfigModels()` and derives `effectiveRoute`, `routeSummaryLabel`, `defaultModel`, ready provider counts, `gatewayUrl`, `runtimeModeLabel`, and `displayedRuntimeBaseUrl`. `accountContent.tsx` confirms local auth facts come from `loadRestorableAuthSession()` and exposes expiry/token type without rendering token values. `shared/studio/models.ts` shows `StudioUserConfig` and `StudioUserConfigModelsResponse` contain only the requested config/provider readiness fields. `config/config.ts` exposes client env values via `process.env.NYXID_*`, `process.env.ORNN_BASE_URL`, `process.env.AEVATAR_CONSOLE_TEAM_FIRST_ENABLED`, and `process.env.AEVATAR_CONSOLE_PUBLIC_PATH`; diagnostics should list only relevant public/config flags already client-defined. Rejected alternatives: adding a backend health endpoint violates scope; creating a reusable diagnostics framework or new shared service is unnecessary; changing Settings shell/shared primitives is broader than needed.

⟦AI:FKST⟧
