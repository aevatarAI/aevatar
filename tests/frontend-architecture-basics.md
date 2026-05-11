# 前端同事版 — Aevatar 架构基础题

> 目标读者：参与 aevatar 页面、控制台、playground、AGUI / WebSocket / API 对接的前端同事
> 答题时长：约 40 分钟
> AI 工具：允许，但请自己核对仓库文档和接口命名

这份题不是后端实现考核。你不需要读完整 C# 调用链，也不需要回答 GAgent 内部状态转移细节。目标是确认你知道：前端什么时候发 command，什么时候读 readmodel，什么时候订阅 observation，以及哪些后端内部事实不该被 UI 硬编码依赖。

## 必读材料

- 根目录 [CLAUDE.md](../CLAUDE.md) 或 [AGENTS.md](../AGENTS.md) 中的：
  - `Command / Envelope / Dispatch`
  - `权威状态 / ReadModel / Projection`
  - `Actor 执行模型` 或 `Actor 化执行哲学`
- [docs/canon/architecture.md](../docs/canon/architecture.md)
- [docs/canon/cqrs-projection.md](../docs/canon/cqrs-projection.md)

答题时不用逐行贴源码，但要能说清楚依据来自哪份文档、哪个小节。总分 60 分，每题 10 分。

## 题 01 — 前端动作应该走哪条链路

把下面 5 个前端需求分别归类为 `Command` / `Query(ReadModel)` / `Observation` / `本地 UI 状态`，并用一句话解释原因。

| 需求 | 你的归类 | 原因 |
|------|----------|------|
| 用户点击"发送消息" | | |
| 页面加载时展示最近 10 条消息 | | |
| 收到 LLM token streaming chunk 并追加到正在生成的气泡 | | |
| 显示"请求已受理，正在处理" | | |
| 用户展开 / 收起侧边栏 | | |

加分点：指出哪一类结果应该带 `stateVersion`、刷新戳或等价的新鲜度信息。

## 题 02 — ACK 不是完成态

后端给发送消息接口返回：

```json
{
  "commandId": "cmd-123",
  "status": "accepted"
}
```

请回答：

1. 前端能不能立刻把消息标成"已完成回复"？为什么？
2. 更合理的 UI 状态流转应该怎么设计？请写出 3 到 5 个状态名。
3. 如果 10 秒内没有收到 observation，前端应该直接判失败、继续等待，还是显示"仍在处理 / 可刷新"？请说明依据。

## 题 03 — 最近消息接口怎么设计才不踩线

产品要一个接口：展示当前会话最近 10 条消息。下面 3 个方案里，选出你愿意接的方案，并指出另外两个哪里不对。

**方案 A**

```text
GET /api/conversations/{id}/actor-state
```

直接返回 ConversationGAgent 内部 state。

**方案 B**

```text
GET /api/conversations/{id}/recent-messages
```

后端在请求里读取 event store，现场 replay 出最近消息。

**方案 C**

```text
GET /api/conversations/{id}/recent-messages
```

后端读取已经物化的 conversation readmodel，并返回 `stateVersion`。

要求：每个方案一句话，不超过 40 字。

## 题 04 — 不要把后端内部命名写死进 UI

下面这些字段或概念，哪些可以成为前端稳定依赖，哪些不应该？请逐条标 `可以 / 不应该 / 视情况`，并解释一句。

- `commandId`
- `actorId` 的字符串前缀，例如 `agent-run-`
- `readmodel.stateVersion`
- 某个 C# 类名，例如 `AgentRunGAgent`
- API 返回的业务状态枚举，例如 `PendingApproval / Running / Completed / Failed`
- `EventEnvelope` 内部路由字段

## 题 05 — 小型 PR Review

下面是一个前端实现草案：

```text
1. 发送消息后，如果 POST 返回 200，就把回复标成 Completed。
2. 每 2 秒请求 /api/conversations/{id}/events/replay，直到读到 Completed。
3. 页面根据 actorId 以 agent-run- 开头来判断这是 LLM 回复。
4. 如果 readmodel 还没有消息，就调用 /api/projections/refresh 再查一次。
```

请挑出至少 3 个问题。每个问题写：

- 违反了哪条架构边界；
- 前端应该怎么改。

## 题 06 — 200 字以内说明题

用不超过 200 字，向另一位前端同事解释：

> 为什么 Aevatar 的前端不能把"发命令成功"、"actor 已经处理完"、"readmodel 已经可查到"混成同一个状态？

要求：必须出现 `Command`、`Observation`、`ReadModel` 三个词；不要写项目符号。

## 答题区
