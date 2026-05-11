---
title: "Discussion 602 Frontend Architecture Exam Answer"
status: draft
owner: potter
source: "https://github.com/aevatarAI/aevatar/discussions/602"
---

# Discussion 602 Frontend Architecture Exam Answer

依据文档：

- `AGENTS.md` / `CLAUDE.md` 的 `Command / Envelope / Dispatch`、`权威状态 / ReadModel / Projection`、`Actor 化执行哲学`
- `docs/canon/architecture.md` 的 `核心主链路`、`CQRS 与 Projection 落点`
- `docs/canon/cqrs-projection.md` 的 `主链路`、`CQRS Core 统一命令骨架`、`投影约束`、`Envelope / Annotation 口径`

## 题 01 — 前端动作应该走哪条链路

| 需求 | 归类 | 原因 |
|------|------|------|
| 用户点击"发送消息" | `Command` | 这是写侧意图，应进入 `Application Command -> Actor Mailbox Message(EventEnvelope)`，由 actor 串行处理并提交领域事实。 |
| 页面加载时展示最近 10 条消息 | `Query(ReadModel)` | 页面读取已经物化的会话读模型；查询不应读取 actor 内部 state，也不应在请求路径 replay event store。 |
| 收到 LLM token streaming chunk 并追加到正在生成的气泡 | `Observation` | streaming chunk 是统一 Projection/AGUI 观察链路输出的实时事件，前端只消费并展示。 |
| 显示"请求已受理，正在处理" | `本地 UI 状态` | 这是基于 accepted receipt 的临时展示状态，只说明命令已受理，不代表 actor committed 或 readmodel observed。 |
| 用户展开 / 收起侧边栏 | `本地 UI 状态` | 这是纯交互偏好，不影响业务事实、命令、投影或读模型。 |

加分点：`Query(ReadModel)` 返回的结果应带 `stateVersion`、刷新戳或等价新鲜度信息；observation 事件也应有可关联的 `commandId/correlationId` 与必要水位，便于 UI 判断当前展示来自哪次命令和哪个物化版本。

## 题 02 — ACK 不是完成态

1. 不能立刻标成"已完成回复"。`accepted` 只承诺 command 已被受理并有稳定 `commandId` 可追踪，不承诺 actor 已处理、领域事件已 committed，也不承诺 readmodel 已物化可查。
2. 更合理的 UI 状态流转：`Composing -> Accepted -> Streaming/Running -> Observed -> Completed`；异常分支可进入 `Failed`，长时间无事件可进入 `StillProcessing` 或 `RefreshAvailable`。
3. 10 秒内没有 observation 时，不应直接判失败。更合理的是显示"仍在处理 / 可刷新"，并保留继续观察与查询 readmodel 的能力。依据是命令 ACK、actor 完成态、readmodel 可见性是分层语义；弱 ACK 不能冒充完成态，readmodel 也可能最终一致。

## 题 03 — 最近消息接口怎么设计才不踩线

方案 A：不接。暴露 `ConversationGAgent` 内部 state，混淆写侧运行态与读侧查询契约。

方案 B：不接。query-time replay event store，违反查询只读已物化 readmodel 的边界。

方案 C：愿意接。读取 conversation readmodel 并返回 `stateVersion`，符合 `Query -> ReadModel`。

## 题 04 — 不要把后端内部命名写死进 UI

- `commandId`：可以。它是 command receipt 与 observation 关联的稳定追踪标识。
- `actorId` 的字符串前缀，例如 `agent-run-`：不应该。`actorId` 是不透明地址，UI 不应解析字面模式判断业务语义。
- `readmodel.stateVersion`：可以。它是读侧新鲜度和物化版本的公开契约。
- 某个 C# 类名，例如 `AgentRunGAgent`：不应该。C# 类型名属于实现细节，不是 API 稳定语义。
- API 返回的业务状态枚举，例如 `PendingApproval / Running / Completed / Failed`：可以。只要它是公开 DTO/API 契约中的业务状态字段，前端可以依赖。
- `EventEnvelope` 内部路由字段：不应该。`EventEnvelope` 的 route/runtime/propagation 是包络级投递与追踪上下文，不是 UI 业务完成语义。

## 题 05 — 小型 PR Review

问题 1：`POST` 返回 200 就把回复标成 `Completed`，违反 ACK 诚实性边界。前端应把它标为 `Accepted` 或 `Processing`，再通过 observation 或 readmodel 结果进入 `Completed`。

问题 2：每 2 秒请求 `/events/replay` 直到读到 `Completed`，违反 query-time replay 与 readmodel 查询边界。前端应订阅 observation 获取实时推进，页面查询则读取已物化 readmodel。

问题 3：根据 `actorId` 以 `agent-run-` 开头判断 LLM 回复，违反 `actorId` 不透明地址原则。前端应依赖 API 返回的强类型业务字段，例如 message role、message kind、source type 或公开状态枚举。

问题 4：readmodel 没有消息就调用 `/api/projections/refresh` 再查，违反不得 query-time priming 的边界。前端应展示 empty / still processing / refresh available，并等待后台 projection 或 observation 推进，不在读路径触发投影生命周期。

## 题 06 — 200 字以内说明题

Aevatar 里 `Command` 只表达一次写侧意图已被受理，并提供可追踪的 `commandId`；actor 是否处理完成，要看后续业务事件和 `Observation`；页面能否稳定查询，则以已经物化的 `ReadModel` 及其 `stateVersion` 为准。三者处在不同一致性层级，混成一个状态会让 UI 误报完成、读到旧数据，甚至依赖后端内部实现。
