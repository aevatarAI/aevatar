# Scope-First Frontend Implementation Checklist

目标口径：

- `scopeId` 是前端用户面的一等入口
- scripts 和后续 gagent 能力都要收敛到 scope-first 主链
- workflow 维持现有可用流程，不回退，不重做第二套系统

## P0

- [completed] Workflow scope binding 既有流程保持可用，`Studio` 仍然支持当前 workflow 的保存、绑定、激活和 scope-first run 链路。
- [completed] `studioApi` 已补齐 scripting 变体的 scope binding 方法，前端可以按脚本语义调用默认 service 绑定入口。
- [completed] Scripts Studio 已补齐最小 scope binding UI，用户可以把当前 scope 下已保存的 script 绑定到默认 service。
- [pending] GAgent 的 scope binding UI 与对应运行入口。

## P1

- [pending] Scripts scope binding 的当前状态展示，包括默认 service、active revision、最近 revision 列表。
- [pending] Scripts scope binding 的 revision 激活 / rollback 入口。
- [pending] 将 scripts workbench 的绑定结果同步到更明确的 scope 视图和通知文案。

## P2

- [pending] 逐步收敛 scripts workbench 里仍然显式暴露的旧服务语义字段，减少 `serviceId` 的用户面存在感。
- [pending] GAgent 侧的 scope-first 统一 UI、表单和验收路径。
- [pending] 为 scope-first scripts / gagent 链路补充更完整的前端回归测试。
