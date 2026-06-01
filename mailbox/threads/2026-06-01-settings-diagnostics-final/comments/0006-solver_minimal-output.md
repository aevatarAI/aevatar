# Approach solver - minimal

role=solver_minimal
thread=2026-06-01-settings-diagnostics-final
approach_round=2
verdict=propose

## Recommended framing
在当前代码事实下，最小可审计边界仍应是只改 `apps/aevatar-console-web/src/pages/settings/index.tsx` 和对应 `index.test.tsx`：现有 Settings 页已经在同一组件内完成 `section` URL 解析、tab rail 键盘导航、`studioApi.getUserConfig`/`getUserConfigModels` 查询、LLM effective route/default model/provider health、runtime base URL 解析与 Account session 展示；Diagnostics 只是把这些已有事实以只读方式汇总并复制为 allowlist Markdown，不需要新增 Settings 本地 component/helper 文件，更不需要新增 API、runtime contract 或跨目录抽象。为降低泄密风险，可以在 `index.tsx` 内放一个小的 `buildDiagnosticsMarkdown` 纯函数，只接收显式非 secret 字段，不把 auth session 或 provider 原对象整体传入。

## Concrete plan
- Files: `apps/aevatar-console-web/src/pages/settings/index.tsx`：把 `SettingsSection` 扩展为 `"llm" | "account" | "diagnostics"`，增加 `diagnosticsTabKey`、URL 读写支持 `/settings?section=diagnostics`、tabDefinitions 第三项、tab refs 初始值和 `handleSectionChange` 分支；复用当前组件内已有 query/draft/runtime/provider 派生值，增加紧凑 Diagnostics section、`Copy diagnostics` 按钮、成功/失败 message，并在同文件内新增只接收 allowlisted 字符串/数字/布尔值的 Markdown builder。
- Files: `apps/aevatar-console-web/src/pages/settings/index.test.tsx`：扩展现有 focused Settings 测试，mock clipboard/message，覆盖 Diagnostics tab URL 状态、session present/missing、provider/model readiness、copy bundle 不含 token/refresh/api key/secret 值，并保留/补充 LLM 与 Account 现有行为断言。
- LOC delta estimate: +220/-15。
- Tests to add/modify: 修改 `apps/aevatar-console-web/src/pages/settings/index.test.tsx`，新增约 5 个 case 或在现有 case 中补断言；无需新增测试文件。
- Rule exception: none。
- Migration path: no migration needed。

## Failure/recovery
- failure_class: none
- recovery: no recovery needed; if implementation drifts into new helper files or backend/runtime paths, current approach gate can re-dispatch or fail conformance from touched path evidence.

## Risks
- `index.tsx` 已经较长，继续 inline 会增加约 200 行局部 UI/formatting 代码；但本次 Diagnostics 没有独立生命周期或复用消费方，拆出 `diagnosticsReport.ts`/`diagnosticsContent.tsx` 会制造额外本地 contract，反而扩大审计面。泄密风险用 allowlisted Markdown builder 和 clipboard 测试控制。

## Escalation triggers
- none

## Reasoning trace
已检查实时文件：`settings/index.tsx` 当前只有 `llm/account` 两个 section，`readSettingsSection` 未识别 diagnostics，tab rail 是本地 button `role=tab` 实现并已支持 Arrow/Home/End；同文件已持有 `userConfigQuery`、`userConfigModelsQuery`、`effectiveRoute`、`routeSummaryLabel`、`draft.defaultModel`、`readyProviderCount`、`displayedRuntimeBaseUrl`、`runtimeModeLabel` 等 Diagnostics 所需事实。`accountContent.tsx` 已用 `loadRestorableAuthSession` 展示 session/token type/expiry，但复制 bundle 不能传 token 原值。`config/config.ts` 只把 `NYXID_*`、`ORNN_BASE_URL`、`AEVATAR_CONSOLE_TEAM_FIRST_ENABLED`、`AEVATAR_CONSOLE_PUBLIC_PATH` 暴露到 client；Diagnostics 应只显示这些已暴露且非 secret 的配置摘要，`NYXID_CLIENT_ID` 可以显示为 configured/missing 而不是复制原值。`models.ts` 的 provider status 只含 slug/name/status/proxyUrl/source，API key 字段不在 `getUserConfigModels` 返回契约内。拒绝拆出新文件的原因是：当前任务没有复用点、没有独立 domain contract、测试可以通过 UI 与 clipboard mock 直接验证，新增 helper/component 文件只会扩大 touched files 与审计范围。未发现 host protected path 或 trusted-boundary escalation 条件。

⟦AI:FKST⟧
