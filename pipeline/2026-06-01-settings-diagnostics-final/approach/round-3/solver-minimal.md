# Approach solver - minimal

role=solver_minimal
thread=2026-06-01-settings-diagnostics-final
approach_round=3
verdict=propose

## Recommended framing
当前 Settings 违规仍然存在：`apps/aevatar-console-web/src/pages/settings/index.tsx` 的 `SettingsSection` 只有 `llm | account`，`readSettingsSection` 只识别 `account`，`tabDefinitions` 只渲染 `LLM/Account`，没有 `Diagnostics`。最小可审计边界应硬收敛在现有 `settings/index.tsx` 与 `settings/index.test.tsx` 两个文件内：页面本身已经持有 `studioApi.getUserConfig`、`studioApi.getUserConfigModels`、LLM route/model/provider 派生值、runtime base URL、tab rail 与 URL 状态，所以新增文件只会引入本地 report/content 契约和 prop 传递，不是排除 secrets 的必要条件；复制安全应通过 `index.tsx` 内非导出的 allowlist Markdown builder 完成，只从已派生的非敏感标量取值，禁止遍历、spread 或 stringify 原始 auth session/config/provider 对象。

## Concrete plan
- Files: `apps/aevatar-console-web/src/pages/settings/index.tsx`：扩展 `SettingsSection`/tab key/URL 解析为 `diagnostics`，新增 Diagnostics tab panel，复用现有 Ant Design Pro/`AevatarPanel`/`SummaryField`/`SummaryMetric` 视觉语言，展示 auth session 状态、token expiry/type、LLM route/model/provider/gateway、runtime mode/base URL/local-remote、公开 frontend env flags、两个现有 query 的 loading/error 摘要；新增非导出的 allowlist Markdown formatter 与 clipboard copy handler，成功/失败用 AntD feedback 提示。
- Files: `apps/aevatar-console-web/src/pages/settings/index.test.tsx`：扩展现有 Settings focused tests，mock auth session、query result/error、clipboard，覆盖 diagnostics URL/tab navigation、present/missing session、provider/model readiness、copy bundle 不包含 token/secret 值，并保留 LLM/Account 既有行为断言。
- LOC delta estimate: +270/-8。
- Tests to add/modify: 修改 `apps/aevatar-console-web/src/pages/settings/index.test.tsx`，预计新增 5 个 focused cases，复用现有默认 mocks；无需新增测试文件。
- Rule exception: none。
- Migration path: no migration needed。

## Failure/recovery
- failure_class: none
- recovery: no recovery needed; if verification fails, current fact sources are sufficient to tighten the local allowlist formatter or test mocks and re-run `pnpm --dir apps/aevatar-console-web test --runInBand settings` plus `pnpm --dir apps/aevatar-console-web tsc`.

## Risks
- `index.tsx` 已经超过 1300 行，继续内联会增加局部文件长度；但本次数据源和 UI 状态都已在该文件内，新增 `diagnosticsReport.ts`/`diagnosticsContent.tsx` 会扩大 touched-file count、创建仅供一次页面使用的契约，并不能比本地 allowlist builder 更好地证明 copied diagnostics 不含 secrets。
- Clipboard 安全依赖实现时坚持显式字段 allowlist；测试必须放入 access token、refresh token、API key/secret 形态的哨兵字符串并断言 copied Markdown 不包含它们。

## Escalation triggers
- none

## Reasoning trace
已核对真实文件：`settings/index.tsx` 当前只有 `llmTabKey`、`accountTabKey` 与两个 tab definitions；`studioApi.getUserConfig` 和 `studioApi.getUserConfigModels` 已在页面内查询；`resolveStudioUserConfigRuntimeBaseUrl`、`normalizeStudioUserConfigRuntimeMode`、`formatStudioUserConfigRuntimeModeLabel` 已在页面内用于 runtime 摘要；`accountContent.tsx` 只展示 Account，不提供 Diagnostics；`shared/auth/session.ts` 提供 `loadRestorableAuthSession` 且 token 字段包含 access/refresh/id token，不能复制；`config/config.ts` 只把 `NYXID_*`、`ORNN_BASE_URL`、`AEVATAR_CONSOLE_TEAM_FIRST_ENABLED`、`AEVATAR_CONSOLE_PUBLIC_PATH` 等前端可见 env 注入 client。拒绝新增 `diagnosticsReport.ts`/`diagnosticsContent.tsx`，因为 secret-exclusion 的关键不是文件拆分，而是 copied Markdown 只由显式非敏感字段组成并由 tests 验证；当前任务不需要 backend API、runtime contract、protected path、架构文档或 host rule 变更。

⟦AI:FKST⟧
