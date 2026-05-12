# Discussion 602 Frontend Architecture Exam Answer

本文只沿用同类 discussion answer 文档的组织形式；答案内容基于题 01 题面、仓库架构规则，以及本次逐题讨论独立整理。

参考：

- `AGENTS.md` / `CLAUDE.md` 的 `Command / Envelope / Dispatch`
- `AGENTS.md` / `CLAUDE.md` 的 `权威状态 / ReadModel / Projection`
- `docs/canon/cqrs-projection.md` 的 CQRS 与 Projection 主链路

## 题 01 - 前端动作应该走哪条链路

这题先不要背术语，可以先按一句话判断：

- 让后端做一件会改变业务事实的事，是 `Command`。
- 问后端现在有哪些已经存在的事实，是 `Query(ReadModel)`。
- 后端做事过程中主动推给前端的进度，是 `Observation`。
- 只影响当前页面显示、不影响后端业务事实，是本地 UI 状态。

| 需求 | 归类 | 原因 |
|------|------|------|
| 用户点击"发送消息" | `Command` | 这是用户要求系统执行一个会改变会话事实的动作。前端相当于在"下单"，后端收到的是一次写入意图，后续由 actor 处理并产生事实。 |
| 页面加载时展示最近 10 条消息 | `Query(ReadModel)` | 这是读取已经存在的消息列表，只是在问"当前会话最近有哪些消息"。页面应该读面向查询准备好的 readmodel，而不是直接看 actor 内部状态。 |
| 收到 LLM token streaming chunk 并追加到正在生成的气泡 | `Observation` | token chunk 是后端生成回复过程中的实时通知。前端只是边收到边展示，它表示"正在生成到这里"，不等于最终消息已经稳定落入查询列表。 |
| 显示"请求已受理，正在处理" | 本地 UI 状态 | 这是前端在后端回复"我收到了"之后显示的 pending 状态。它说明请求已进入处理流程，但不能说明处理已经完成，也不能说明最近消息 readmodel 已经刷新。 |
| 用户展开 / 收起侧边栏 | 本地 UI 状态 | 这只是当前页面的交互状态，默认不改变业务事实，不需要 command、readmodel 或 observation 参与。 |

## 加分点 - 哪类结果应该带新鲜度信息

`Query(ReadModel)` 返回的结果应该带 `stateVersion`、`refreshedAt` 或等价的新鲜度信息。

这里的"新鲜度信息"可以理解为：

> 这份查询结果更新到后端事实的哪一步了？

例如，用户发送消息以后，后端可能已经受理 command，但最近消息列表还没有立刻刷新。如果接口只返回 `messages`，前端就不知道自己看到的是新列表还是旧列表。若接口同时返回 `stateVersion`：

```json
{
  "stateVersion": 11,
  "messages": [
    {
      "role": "user",
      "text": "帮我总结一下"
    }
  ]
}
```

前端就能知道这份消息列表对应会话事实的第 11 版。`refreshedAt` 也是类似思路，只是用刷新时间表达"这份数据大概有多新"。

因此这题的关键不是字段必须叫什么名字，而是 readmodel 查询结果要诚实暴露自己的物化版本或刷新时间。前端不能把 command 的 `accepted` 当成"数据已经最新"，也不能把 readmodel 暂时没追上误判成后端没有处理。

## 题 02 - ACK 不是完成态

题目里的响应是：

```json
{
  "commandId": "cmd-123",
  "status": "accepted"
}
```

这里的 `accepted` 可以先理解成后端说：

> 我收到了你的请求，这是这次请求的编号。

它不等于：

- actor 已经处理完成。
- LLM 已经生成完回复。
- 最近消息 readmodel 已经刷新。

### 1. 前端能不能立刻标成"已完成回复"

不能。

`accepted` 只表示发送消息这个 command 已经被后端受理，并返回了可追踪的 `commandId`。它不代表 actor 已经处理完成，不代表 LLM 回复已经完成，也不代表 readmodel 已经更新。

前端收到这个响应后，最多可以进入"已受理 / 正在处理"状态；"已完成回复"必须由后续 observation 或 readmodel 中的完成态来证明。

### 2. 更合理的 UI 状态流转

一个更合理的状态流转可以是：

```text
Sending -> Accepted -> Streaming -> Completed
```

异常时可以从 `Sending`、`Accepted` 或 `Streaming` 进入 `Failed`。

| 状态 | 含义 |
|------|------|
| `Sending` | 前端正在把发送消息请求提交给后端，还没拿到 accepted。 |
| `Accepted` | 后端已经接收请求并返回 `commandId`，但还没有证明业务处理完成。 |
| `Streaming` | 前端正在收到 LLM token observation，回复还在生成中。 |
| `Completed` | 收到完成 observation，或从 readmodel 查到这条回复已经是完成态。 |
| `Failed` | 收到明确失败事件、查询到失败态，或超过产品定义的最终超时策略。 |

如果 UI 文案不想暴露 `Accepted` 这个词，也可以显示成"正在处理"。关键点是：这个状态只能表达"请求已进入处理流程"，不能表达"回复已完成"。

### 3. 10 秒没有 observation 时怎么办

不应该 10 秒没有 observation 就直接判失败。

10 秒没有 observation 只能说明前端暂时没有观察到后续进度，不能证明后端已经失败。可能是 LLM 首 token 慢、observation 通道延迟、任务排队、网络抖动，或者 readmodel 还没刷新。

更合理的 UI 行为是：

- 继续保持 observation 订阅。
- 显示"仍在处理"或"可刷新"。
- 允许用户通过正常的 readmodel 查询刷新当前结果。
- 只有收到明确失败事件、查询到失败态，或超过产品定义的最终超时策略，才进入 `Failed`。

这里的"刷新"是重新读取正常的 readmodel 查询接口，不是让读路径触发 projection refresh。前端不能因为暂时没有 observation，就绕过 readmodel 或强行驱动后端投影生命周期。

### 可提交答案

```markdown
1. 不能立刻标成"已完成回复"。`accepted` 只表示发送消息这个 command 已经被后端受理，并返回了可追踪的 `commandId`；它不代表 actor 已经处理完成，不代表 LLM 回复已经完成，也不代表 readmodel 已经更新。

2. 更合理的 UI 状态可以是：`Sending`、`Accepted`、`Streaming`、`Completed`、`Failed`。`Sending` 表示前端正在提交请求；`Accepted` 表示后端已经接收但还没完成；`Streaming` 表示前端正在收到 token observation；`Completed` 必须由完成 observation 或 readmodel 中的完成态确认；`Failed` 只能由明确失败事件、失败查询结果或产品定义的最终超时策略触发。

3. 如果 10 秒内没有收到 observation，不应该直接判失败。更合理的是显示"仍在处理 / 可刷新"，继续等待 observation，并允许通过正常的 readmodel 查询刷新结果。因为 command ACK、后端处理完成、readmodel 可查询是三层不同语义，`accepted` 和"暂时没有 observation"都不能直接推出"已完成"或"已失败"。
```

## 题 03 - 最近消息接口怎么设计

产品要一个接口展示当前会话最近 10 条消息，三个方案里应选择方案 C。

```markdown
方案 A：不接；直接暴露 `ConversationGAgent` 内部 state，破坏 actor 边界和读写分离。

方案 B：不接；在 query 请求里现场 replay event store，违反查询只读 readmodel 的规则。

方案 C：接；接口读取已经物化的 conversation readmodel，并返回 `stateVersion`，符合 `Query -> ReadModel`。
```

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
