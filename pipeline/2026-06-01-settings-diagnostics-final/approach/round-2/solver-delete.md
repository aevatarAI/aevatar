# Approach solver - delete

role=solver_delete
thread=2026-06-01-settings-diagnostics-final
approach_round=2
verdict=propose

## Classification
d - 诊断页签是有效能力缺口，但不需要新增后端、runtime contract、跨目录模块或 settings-local 组件层；现有 Settings 页已经持有所需事实，应删除/拒绝额外抽象，只保留小的本地呈现与显式 allowlist copy bundle。

## Recommended action
建议实现，但收窄为 `apps/aevatar-console-web/src/pages/settings/index.tsx` 与 `index.test.tsx` 两文件内的本地改动：新增 `diagnostics` section key、第三个 tab、一个紧凑 Diagnostics section 和一个显式字段 allowlist 的 Markdown copy builder；不新增 `diagnosticsReport.ts`、`diagnosticsContent.tsx` 或共享诊断模型。避免新增文件是本轮的硬边界，除非实现中出现 TypeScript/测试不可维护的实际失败；即使需要提取，也只允许在 `index.tsx` 内提取非导出的纯 formatter/builder 函数，不创建可复用 API。

## Concrete plan
- Files to delete: none
- Files to edit: `apps/aevatar-console-web/src/pages/settings/index.tsx`; `apps/aevatar-console-web/src/pages/settings/index.test.tsx`
- Caller migrations: none; only extend the existing Settings section union, URL parser, tab definitions, tab refs, header extra switch, content description switch, and tabpanel branch to include `diagnostics`
- Tests to delete/update/add: keep existing LLM and Account tests; add focused Settings tests for `?section=diagnostics` navigation, present/missing auth session, provider/model summary, copy success/failure feedback, and copied Markdown excluding `accessToken`/`refreshToken`/`idToken`/`apiKey`/secret values
- LOC delta estimate: +240/-10
- Migration path: one-step Settings-local tab addition; no compatibility shim and no new route/API contract

## Reverse-evidence
- Settings currently has only two sections: `SettingsSection = "llm" | "account"` in `apps/aevatar-console-web/src/pages/settings/index.tsx:72`, `readSettingsSection` falls back to LLM unless `section=account` at `apps/aevatar-console-web/src/pages/settings/index.tsx:184`, and `tabDefinitions` only contains LLM/Account at `apps/aevatar-console-web/src/pages/settings/index.tsx:965`.
- The existing tab rail is already keyboard-addressable by data, so adding one key is sufficient: arrow/home/end traversal uses `tabDefinitions.length` at `apps/aevatar-console-web/src/pages/settings/index.tsx:978`, and button refs are keyed by `SettingsSection` at `apps/aevatar-console-web/src/pages/settings/index.tsx:972`.
- The page already fetches the required API facts with existing calls: `studioApi.getUserConfig()` and `studioApi.getUserConfigModels()` are used at `apps/aevatar-console-web/src/pages/settings/index.tsx:561` and `apps/aevatar-console-web/src/pages/settings/index.tsx:565`; no backend API is needed.
- LLM/runtime facts already exist in the page state: effective route at `apps/aevatar-console-web/src/pages/settings/index.tsx:659`, runtime base URL at `apps/aevatar-console-web/src/pages/settings/index.tsx:712`, runtime mode label at `apps/aevatar-console-web/src/pages/settings/index.tsx:720`, provider health/counts at `apps/aevatar-console-web/src/pages/settings/index.tsx:724`, and technical preview rows at `apps/aevatar-console-web/src/pages/settings/index.tsx:856`.
- Account session facts already exist without exposing token values: `loadRestorableAuthSession()` is used in `apps/aevatar-console-web/src/pages/settings/accountContent.tsx:39`, and the UI displays expiry/type/scope/refresh availability rather than raw token strings at `apps/aevatar-console-web/src/pages/settings/accountContent.tsx:142`.
- Raw secrets are present in the auth type and must not be spread into diagnostics: `accessToken`, `refreshToken`, and `idToken` are fields in `apps/aevatar-console-web/src/shared/auth/session.ts:4`.
- Provider/model diagnostics can be built from the existing typed response: provider status fields are defined in `apps/aevatar-console-web/src/shared/studio/models.ts:787`, and `gatewayUrl`, `modelsByProvider`, `supportedModels` are defined in `apps/aevatar-console-web/src/shared/studio/models.ts:795`.
- Frontend env facts already exposed to the client are bounded by Umi `define`: `NYXID_*`, `ORNN_BASE_URL`, `AEVATAR_CONSOLE_TEAM_FIRST_ENABLED`, and `AEVATAR_CONSOLE_PUBLIC_PATH` are injected at `apps/aevatar-console-web/config/config.ts:150`; the public path is configured at `apps/aevatar-console-web/config/config.ts:27`.
- Existing copy UX patterns are local and small: `WorkflowYamlViewer` uses `navigator.clipboard.writeText` plus Ant message success/error at `apps/aevatar-console-web/src/pages/workflows/WorkflowYamlViewer.tsx:256`, so no shared clipboard abstraction is justified.
- Current tests only cover the two existing tabs and LLM behavior, e.g. default LLM at `apps/aevatar-console-web/src/pages/settings/index.test.tsx:103` and Account navigation at `apps/aevatar-console-web/src/pages/settings/index.test.tsx:120`; adding diagnostics assertions belongs in the same focused test file.

## Failure/recovery
- failure_class: none
- recovery: no recovery needed; behavior is covered by existing Settings tests plus the requested focused additions, and failed copy/API states remain observable through UI assertions

## Risks
- Inline implementation can become noisy if it duplicates LLM summary rendering; cap it by reusing `SummaryField`, `SummaryMetric`, `AevatarPanel`, existing computed values, and only local formatter helpers.
- Copy diagnostics is the main secret-leak risk; do not serialize `authSession`, provider settings, raw API responses, or arbitrary objects. Build the Markdown from literal allowlisted lines only.
- Reading `loadRestorableAuthSession()` once via memo may not reflect a session change during the same mounted Settings page; acceptable for this diagnostics tab unless existing Account behavior is changed at the same time.
- `process.env.NYXID_CLIENT_ID` is client-exposed but still an identifier; include it only if product wants auth config visibility. The minimal safe frontend env list can start with public path and boolean feature flags, then omit auth/client IDs from the copy bundle.

## Escalation triggers
- none

## Reasoning trace
I rejected a false-positive/do-nothing answer because `rg` shows no Settings diagnostics tab or copy diagnostics action, and the current `SettingsSection` union plus URL parser only recognize `llm` and `account`. I rejected deleting Account/Technical preview because they are active surfaces with tests and do not provide API loading/error summaries or a copyable support bundle. I rejected new settings-local files because all required facts are already in the Settings page render scope or existing Settings account component, and introducing `SettingsDiagnosticsReport`/`diagnosticsContent.tsx` would create a reusable-looking contract for a one-page operator panel. The maximum acceptable extraction is non-exported pure functions inside `index.tsx` such as `formatDiagnosticValue` and `buildDiagnosticsMarkdownFromAllowlist`; if that is still too large, the implementation should reduce fields before adding files.

⟦AI:FKST⟧
