# 题 06 — 读写分离 + Memory 模块去留

> 满分：10 分
> 必读：
> - 根目录 `CLAUDE.md` §"权威状态 / ReadModel / Projection（强制）"
> - [docs/canon/cqrs-projection.md](../docs/canon/cqrs-projection.md)
> - Discussion #568 §3 "Memory 作废"全文（含 loning 的两条 reply）
> - `agents/Aevatar.GAgents.UserMemory/UserMemoryGAgent.cs`
> - `src/Aevatar.Studio.Infrastructure/ActorBacked/ActorBackedUserMemoryStore.cs`

## 题面

### 6.1（5 分）读写分离的应用题

新同事 A 在 review 中收到这样的需求："前端要展示**当前会话的最近 10 条消息**，给我一个接口。" 他写出下面三个候选：

**候选 1**

```csharp
public interface IConversationGAgent : IGAgent
{
    Task<IReadOnlyList<ConversationMessage>> GetRecentMessagesAsync(int limit);
}
```

**候选 2**

```csharp
public interface IConversationQueryService
{
    Task<RecentMessagesDto> GetRecentAsync(string conversationId, int limit, CancellationToken ct);
}
// 实现里：先 IEventStore.LoadAsync(conversationId) 拿到所有事件，
//        ConversationState.ReplayFrom(events)，
//        再裁剪后映射成 DTO 返回。
```

**候选 3**

```csharp
public interface IConversationQueryPort
{
    Task<RecentMessagesDto> GetRecentAsync(string conversationId, int limit, CancellationToken ct);
}
// 实现里：调用 IProjectionDocumentReader 读 conversation_messages readmodel，
//        附带返回 readmodel 的 stateVersion。
```

请回答：

- (a) **三个候选各自合规 / 不合规**，每一个写一句 ≤ 30 字 的判断。
- (b) 对**最不合规**的那一个，引 CLAUDE.md 中**两条以上不同**的强制规则证明它"不止违反一条"。
- (c) 假设产品再加一条："要返回\"当前 LLM 是否还在思考\"这个布尔值。" 请**分两种语义**回答（两段加起来 ≤ 80 字）：
  - **情形 1**：把它当作**最近消息列表 DTO（候选 3 的返回值）上的字段**。它该不该挂在这里？依据什么规则？
  - **情形 2**：把它作为**独立的 run 当前态查询字段**（例如 `run_current_state` readmodel 上的 `is_sampling` 布尔）。这样合规吗？如果合规，对前端"瞬时流式态"的需求是不是合适？
  - 两种语义可能结论不同。请明确每种情形依据 cqrs-projection.md / CLAUDE.md 哪条规则（"权威源版本 / 刷新戳 / 弱读"等）。

### 6.2（5 分）UserMemoryGAgent 是不是 #568 §3 反对的"Memory 模块"？

打开 `agents/Aevatar.GAgents.UserMemory/UserMemoryGAgent.cs`。Discussion #568 §3 的最终结论是 **"Memory 作为独立 Harness 模块作废"**，理由摘录：

> "Memory" 作为独立 Harness 模块是 trivial 抽象——它只是给已有结构（GAgent state + committed event log + projection / readmodel）起了个新名字...

但是仓库里**真实存在** `UserMemoryGAgent`、`UserMemoryState`、`MemoryEntryAddedEvent` 等命名包含 "Memory" 的对象。这是不是与 §3 矛盾？

请按下列结构作答（每段 ≤ 60 字）：

- (a) **类比论证**：§3 给出过一张"看起来像 memory 的需求 vs Aevatar 已有结构"的映射表，请把 `UserMemoryGAgent` **放入这张表**对应的某一行；说明它对应的是哪一行的"需求"和哪一行的"已有结构"。
- (b) **结构性判断**：`UserMemoryGAgent` 的状态类型（`UserMemoryState`）是 protobuf message，事件通过 `PersistDomainEventAsync` 走 ES。从这两点判断它是不是 §3 反对的"旁挂式 sidecar memory（vector DB / chat history / sidecar）"。
- (c) **命名 vs 抽象**：§3 把 trivial 抽象的代价之一描述为 *"新词覆盖已有结构，认知负担 +1"*。`UserMemoryGAgent` 这个**命名**有没有犯这个错？如果有，建议改成什么名字、或者改成什么继承关系来淡化"Memory 是独立模块"的暗示？
- (d) **结论**：在 1 分（"完全是 §3 反对的对象"）到 5 分（"完全合规、命名也无可指摘"）之间，给 `UserMemoryGAgent` 一个**整数评分**并说明理由。

> 提示：这是开放题。结论本身不计分（1～5 都可能被接受），评分点是 (a)(b)(c) 是否言之有据。

## 答题区
