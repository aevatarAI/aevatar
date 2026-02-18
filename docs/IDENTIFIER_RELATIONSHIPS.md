# 标识符关系（EventEnvelope.Id / CommandId / SessionId / ActorId / ProjectionId）

本文描述当前实现的标识符边界。已移除 Workflow 业务 `RunId` 语义。

## 一句话结论

| 标识符 | 作用域 | 谁生成 | 主要用途 |
|---|---|---|---|
| `EventEnvelope.Id` | 单条事件 | 事件发布方 | 事件级去重与追踪 |
| `CommandId` | 一次命令受理 | `ICommandContextPolicy` | 写侧请求追踪与读侧快照关联 |
| `SessionId` | 一段 AI 对话上下文 | AI/应用层 | 维护消息上下文连续性 |
| `ActorId` | 一个 Actor 实例 | Runtime | 事件订阅粒度、并发边界、查询入口 |
| `ProjectionId` | 一个投影上下文实例 | 投影层 | 读模型投影上下文标识（当前默认等于 `ActorId`） |

## 生成点（代码）

- `CommandId`
  - `src/Aevatar.CQRS.Core/Commands/DefaultCommandContextPolicy.cs`
  - 写入 metadata：`command.id`
- `ActorId`
  - 由 Runtime 维护并用于 stream 订阅键。
- `ProjectionId`
  - `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionService.cs`
  - 当前采用 actor 共享投影语义：`ProjectionId = ActorId`。
- `SessionId`
  - 用于 AI 消息链路（不是 workflow 执行隔离标识）。

## 查询契约

- `GET /api/chat/workflows`
- `GET /api/chat/actors/{actorId}`
- `GET /api/chat/actors/{actorId}/timeline`

## 设计说明

- CQRS.Core 不承载 workflow 业务标识语义。
- 同一 Actor 的多次调用聚合到同一读模型，按事件时间线持续更新。
- AGUI 的 `RunId` 字段仅用于协议对齐，当前映射为 `threadId/actorId`，不作为 workflow 业务主键。
