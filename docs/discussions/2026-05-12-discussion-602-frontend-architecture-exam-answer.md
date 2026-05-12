# Discussion 602 Frontend Architecture Exam Answer

## 题 01 - 前端动作应该走哪条链路

| 需求 | 归类 | 原因 |
|------|------|------|
| 用户点击"发送消息" | `Command` | 发送消息是写侧意图，会改变会话事实，应由后端 command/actor 链路处理。 |
| 页面加载时展示最近 10 条消息 | `Query(ReadModel)` | 读取最近消息属于查询，应读取已物化 readmodel，而不是 actor 内部状态。 |
| 收到 LLM token streaming chunk 并追加到正在生成的气泡 | `Observation` | token chunk 是生成过程中的实时观察事件，不等同于最终 committed 消息。 |
| 显示"请求已受理，正在处理" | 本地 UI 状态 | 这是基于 accepted ACK 的 pending 展示，只表示请求已受理，不代表处理完成或 readmodel 已刷新。 |
| 用户展开 / 收起侧边栏 | 本地 UI 状态 | 仅影响当前页面展示，不改变后端业务事实。 |

## 加分点 - 哪类结果应该带新鲜度信息

`Query(ReadModel)` 返回的结果应该带 `stateVersion`、`refreshedAt` 或等价的新鲜度信息。

readmodel 是最终一致的查询副本，返回结果需要暴露物化版本或刷新时间，便于 UI 判断当前列表是否已经追上某次 command。`accepted` ACK 不能被当成 readmodel 已刷新的证明。

## 题 02 - ACK 不是完成态

### 1. 前端能不能立刻标成"已完成回复"

不能。`accepted` 只表示 command 已被后端受理，并返回了可追踪的 `commandId`；它不代表 actor 已处理完成、LLM 回复已完成或 readmodel 已更新。

### 2. 更合理的 UI 状态流转

推荐状态流转：`Sending -> Accepted/Processing -> Streaming -> Completed`；异常时从 `Sending`、`Accepted/Processing` 或 `Streaming` 进入 `Failed`。

| 状态 | 含义 |
|------|------|
| `Sending` | 前端正在提交请求。 |
| `Accepted/Processing` | 后端已接收请求并返回 `commandId`，但尚未证明业务处理完成。 |
| `Streaming` | 前端正在收到 LLM token observation，回复还在生成中。 |
| `Completed` | 收到完成 observation，或从 readmodel 查到这条回复已经是完成态。 |
| `Failed` | 收到明确失败事件、查询到失败态，或超过产品定义的最终超时策略。 |

### 3. 10 秒没有 observation 时怎么办

不应该直接判失败。10 秒无 observation 只能说明 UI 暂未观察到进度；应保持 observation 订阅，显示"仍在处理 / 可刷新"，并允许通过正常 readmodel 查询重读结果。只有收到明确失败事件、查询到失败态，或超过产品定义的最终超时策略，才进入 `Failed`。

## 题 03 - 最近消息接口怎么设计

产品要一个接口展示当前会话最近 10 条消息，三个方案里应选择方案 C：

- 方案 A：不接；直接暴露 `ConversationGAgent` 内部 state，破坏 actor 边界和读写分离。
- 方案 B：不接；在 query 请求里现场 replay event store，违反查询只读 readmodel 的规则。
- 方案 C：接；接口读取已经物化的 conversation readmodel，并返回 `stateVersion`，符合 `Query -> ReadModel`。

## 题 04 - 不要把后端内部命名写死进 UI

| 字段或概念 | 判断 | 原因 |
|------------|------|------|
| `commandId` | 可以 | 它是公开 receipt 里的稳定追踪标识，前端可以用来关联一次 command 和后续 observation。 |
| `actorId` 的字符串前缀，例如 `agent-run-` | 不应该 | `actorId` 是不透明地址，前端不应解析字符串前缀来推断业务语义。 |
| `readmodel.stateVersion` | 可以 | 它是 readmodel 对外暴露的新鲜度/版本信息，前端可以用来判断查询结果更新到哪个事实版本。 |
| 某个 C# 类名，例如 `AgentRunGAgent` | 不应该 | C# 类名是后端实现细节，不是前端可依赖的 API 契约。 |
| API 返回的业务状态枚举，例如 `PendingApproval / Running / Completed / Failed` | 可以 | 这是公开 API DTO 中的业务语义，前端可以据此渲染状态和交互。 |
| `EventEnvelope` 内部路由字段 | 不应该 | 这些字段属于后端投递/运行时细节，不应该作为 UI 的业务判断依据。 |

## 题 05 - 小型 PR Review

| 问题 | 违反的架构边界 | 前端应该怎么改 |
|------|----------------|----------------|
| 发送消息后，`POST` 返回 200 就把回复标成 `Completed`。 | 混淆 command ACK 和业务完成态。`accepted` 或 HTTP 200 只能说明请求已被受理，不能说明 actor 已处理完成，也不能说明 readmodel 已更新。 | `POST` 成功后只进入 `Accepted` / `Processing`；等完成 observation，或从 readmodel 读到完成态后，再标成 `Completed`。 |
| 每 2 秒请求 `/api/conversations/{id}/events/replay`，直到读到 `Completed`。 | 把 query 路径变成 query-time replay，绕开正常的 readmodel 查询和 observation 订阅。 | 实时进度走 observation；页面查询走已物化的 readmodel。不要在普通前端轮询里 replay event store。 |
| 页面根据 `actorId` 以 `agent-run-` 开头来判断这是 LLM 回复。 | 依赖 actor 地址的字符串形状，违反 `actorId` 不透明地址原则。 | 依赖公开 API DTO 中的业务字段，例如 `messageRole`、`messageKind`、`sourceType` 或 `status`。 |
| 如果 readmodel 还没有消息，就调用 `/api/projections/refresh` 再查一次。 | 在查询路径触发 projection lifecycle，属于 query-time priming。 | 显示空状态、处理中或可刷新；继续等待 observation 或通过正常 readmodel 查询重读结果，不由 UI 强行刷新投影。 |

## 题 06 - 200 字以内说明题

Command 成功只表示后端受理了一次写入意图，有 `commandId` 可追踪；actor 是否推进完成，要等 Observation 或业务完成事件；页面能否稳定读到结果，要看 ReadModel 是否已物化到相应版本。三者一致性层级不同，混成一个状态会让 UI 误报完成、读到旧数据或依赖内部实现。
