# Aevatar.CQRS.Projection.Core.Abstractions

`Aevatar.CQRS.Projection.Core.Abstractions` 只包含投影运行时主链路的通用抽象，不包含任何 ReadModel/Graph provider 选择与存储能力。

## 目录结构

- `Abstractions/Core`：`IProjectionContext`、`IProjectionRuntimeOptions`、`IProjectionClock`、`IProjectionStreamSubscriptionContext`
- `Abstractions/Pipeline`：投影编排主链路契约（`Coordinator/Dispatcher/Applier/Projector/Lifecycle`）
- `Abstractions/Ports`：投影端口协作接口（activation/release/sink 管理）
- `Abstractions/Streaming`：会话事件与 actor stream 订阅抽象

## 约束

1. 不包含 provider 选择策略、capability 校验与 store factory。
2. 不包含业务 read model、业务 context、DI 装配或具体存储实现。
3. 仅定义稳定运行时协议，面向跨业务复用。
