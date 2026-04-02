# Scope-First Frontend Implementation Checklist

目标口径：

- `scopeId` 是前端用户面的一等入口
- scripts 和后续 gagent 能力都要收敛到 scope-first 主链
- workflow 维持现有可用流程，不回退，不重做第二套系统

## P0

- [completed] Workflow scope binding 既有流程保持可用，`Studio` 仍然支持当前 workflow 的保存、绑定、激活和 scope-first run 链路。
- [completed] `studioApi` 已补齐 scripting 变体的 scope binding 方法，脚本可以按 scope-first 语义绑定到默认 service。
- [completed] Scripts Studio 已补齐最小 scope binding UI，用户可以把当前 scope 下已保存的 script 绑定到默认 service。
- [completed] Scripts binding API、workbench 回归测试和 `tsc` 均已通过，当前请求契约已切到嵌套 `script` 载荷。
- [completed] `Runs` 页已收敛成通用 `service endpoint` 工作台，保留 `draft-run` 和 `chat:stream`，并补了通用 `invoke/{endpointId}`。
- [completed] Scripts `Test Run` 已恢复为真正的 draft run：直接走 `/api/app/scripts/draft-run`，不会 rebinding 当前 scope 默认 service，也不要求先打开 `Runs` 页。
- [completed] `Runs` 页的 service invocation draft helper、页面跳转和关键回归测试已补上；workflow / gagent 可以把默认 scope service 的调用载荷直接带入运行台。
- [in_progress] 共享 binding DTO 已开始从 workflow 专用形状扩成多形态结果，但 workflow-centric 文案和调用方还没完全收敛。
- [completed] GAgent 的 Studio scope binding UI 与运行入口已补齐，可以在 Studio 里绑定静态 GAgent 并直接打开 `Runs` 页执行指定 endpoint。

## P1

- [completed] Scripts scope binding 的当前状态展示已补齐，包括默认 service、active revision、最近 revision 列表。
- [completed] Scripts scope binding 的 revision 激活 / rollback 入口已补到 `Scope Scripts` 页。
- [completed] 已补一个真正的 `scope -> 当前 binding/service` 总览页，作为 `Scopes` 的 landing page，集中展示 binding、revision、workflow/script 资产和 Studio/Runs 快捷入口。
- [pending] 将 scripts workbench 的绑定结果同步到更明确的 scope 视图和通知文案。
- [pending] Runs 历史仍是浏览器 `localStorage`，还没有 scope 级 read model 驱动的正式 run 列表 / 恢复 / 审计能力。

## P2

- [pending] 逐步收敛 scripts workbench、Runs 页和共享层里仍然显式暴露的旧 workflow/service 语义字段，减少 `workflowId / workflowName / serviceId` 的用户面存在感。
- [completed] GAgent 侧的 scope-first 统一 UI、表单和最小验收路径已补齐，包含绑定、带 endpoint 的运行入口和关键页面回归测试。
- [pending] 版本治理仍只有 activate/rollback，缺 `deprecated / disabled`、原因说明、diff 和禁止调用态治理动作。
- [completed] scope-first scripts / gagent 链路的关键前端回归测试已补齐，覆盖 shared API、page handoff 和 Studio 入口。
