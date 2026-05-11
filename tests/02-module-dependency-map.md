# 题 02 — 一次 Lark 群消息回复的模块依赖图

> 满分：10 分
> 必读：
> - `agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.cs`
> - `agents/Aevatar.GAgents.Channel.Runtime/Conversation/IChannelLlmReplyRunDispatcher.cs`
> - `agents/Aevatar.GAgents.NyxidChat/AgentRunDispatcher.cs`
> - `agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.cs`
> - `src/Aevatar.AI.Core/Chat/ChatRuntime.cs`
> - `src/Aevatar.AI.Core/Tools/ToolCallLoop.cs`
> - `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/`
> - [docs/adr/0009-channel-bot-callback-architecture.md](../docs/adr/0009-channel-bot-callback-architecture.md)
> - [docs/adr/0011-lark-nyx-relay-webhook.md](../docs/adr/0011-lark-nyx-relay-webhook.md)
> - [docs/adr/0014-interactive-reply-abstraction.md](../docs/adr/0014-interactive-reply-abstraction.md)
> - 提示：Issue #596 的 "目标架构" mermaid 是这条链路的**理想形态**抽象，但**当前代码已经走完了 Phase A**。请以仓库现状为准。

## 题面

一条用户消息从飞书侧发出，经 NyxID Relay 进入 aevatar，再产生一次 LLM 回复推回飞书。请回答下列问题。

### 2.1（4 分）画依赖链

按**调用 / 投递顺序**列出从 "NyxID Relay 调到 aevatar HTTP 端点" 起，到 "回执 chunk 走出 aevatar 进程" 为止，**消息穿过的每一个有名字的角色**。每一行写：

```
N. <项目相对路径>::<类型/接口名> — 它在这一拍干了什么（≤ 15 字）
```

要求：
- **不少于 7 行**，**不多于 12 行**。少写漏环节扣分，多写无关层也扣分。
- 至少出现一次 `EventEnvelope` 字眼或与之等价的投递载体说明。
- 如果一拍是**异步事件而非同步调用**，请在该行末尾用 `[event]` 标注。

### 2.2（3 分）边界

回答以下三个问题，每问 ≤ 30 字，必须**点到具体类型/方法名**：

- (a) `ConversationGAgent` **拒绝**承担哪一类执行职责？把那个职责现在落到哪个类承担？
- (b) `AgentRunGAgent` 用什么作为它的 `actorId`？这个 `actorId` 提供的是**寻址/幂等**还是**stale 守门**？stale 请求由**另一个**什么机制丢弃？请给出**两处源码 file::line**——一处指向 actorId 构造，一处指向 stale 实际判定。
- (c) 新进来的 `NeedsLlmReplyEvent` 从 `ConversationGAgent` 投到 `AgentRunGAgent` **有没有**经过 Projection Pipeline？为什么？

### 2.3（3 分）演进状态识别

下列**5 个名字**中：
1. `ChannelLlmReplyInboxRuntime`
2. `IChannelLlmReplyRunDispatcher`
3. `IChannelLlmReplyInbox`
4. `AgentRunGAgent`
5. `ToolCallLoop`

请回答：

- 先给出你实际跑过的 `grep` 或 `rg` 命令。
- 把 5 个名字分别归到下面 4 类之一，并各写一句证据：
  - `当前主链路`：一次 Lark 回复现在真的会经过它。
  - `保留的边界 / 适配点`：还在为当前链路服务，但不是执行主体（典型如 IO worker / 适配 port）。
  - `历史 / 已下线`：当前仓库不存在，或只剩文档、历史引用、测试迁移痕迹。
  - `待后续收敛`：当前仍在用，但 Issue #596 / Discussion #568 已把它标成历史包袱或后续拆解对象。
- 至少 2 个名字必须引用源码路径，至少 1 个名字必须引用 Issue #596 的原文或 `gh issue view` 输出。

> 说明：4 类中**允许有某一类为空**（只要 5 个名字全部分类完毕、证据成立即可）。如果你判定某类为空，请在答题区末尾写一句 *"X 类为空，因为 …"* 说明。把名字硬塞进不合适的类反而扣分。

## 答题区
