# 题 04 — Trivial 抽象判定

> 满分：10 分（每小题 2.5 分 × 4）
> 必读：
> - Discussion #568 §3（"Memory 模块作废"那段，特别是"trivial 抽象的代价"四点）
> - Discussion #568 §4（"AgentGovernanceProfile 不在四层中"那段）
> - Discussion #568 §5（"TeamManagerGAgent / TaskBoardGAgent 退回 example"那段）
> - 根目录 `CLAUDE.md` §"删除优先" / §"架构哲学 — 单一主干，插件扩展"

## 题面

Discussion #568 反复用一个判断方式：**"如果一个抽象只是给已有结构起新名字，没产生新约束 / 新能力，它就是 trivial（平凡）抽象，不值得引入。"** loning 把它和数学里的"平凡解"做了类比。

下面是 4 个**新同事提案**。每个提案请回答：

- (a) **是 / 不是 / 视情况** trivial 抽象？
- (b) 如果是，它**重命名/包装**了仓库里**现有的哪几个**结构？请给出**具体类型名 / 接口名**（至少 2 个）。
- (c) 如果你判 "不是 trivial"，必须说明它**产生了什么新约束或新能力**（一句即可），并指出它会落地到 CLAUDE.md 中**哪个分层**。
- (d) 给出一个最终建议：**接受 / 拒绝 / 改写为...**。

判断错（a 给反）直接 0 分。其他每小问 0.5～1 分。

---

### 提案 1：`AgentGovernanceProfile`

新同事提议在 Application 层引入：

```csharp
public sealed record AgentGovernanceProfile(
    string AgentId,
    PermissionMode Mode,
    IReadOnlyList<ToolRule> ToolRules,
    IReadOnlyList<ConnectorRule> ConnectorRules,
    BudgetRules Budget,
    ApprovalPolicy Approval);

public interface IAgentPolicyEvaluator
{
    Task<PolicyDecision> EvaluateAsync(AgentInvocation invocation, CancellationToken ct);
}
```

理由："这样我们就能集中治理。"

### 提案 2：`ICapabilityRegistry`

新同事提议把所有 `IAgentToolSource` 的工具枚举包装成一个统一的 `ICapabilityRegistry`：

```csharp
public interface ICapabilityRegistry
{
    Task<IReadOnlyList<CapabilityDescriptor>> ListAsync(AgentScope scope, CancellationToken ct);
    Task<CapabilityHandle> ResolveAsync(string capabilityId, CancellationToken ct);
}
```

`CapabilityDescriptor` 内部字段就是 `IAgentTool` 已有的 name / args schema / `IsReadOnly` / `RequiresApproval`，加上一个 `source` 字段标记是 NyxID-backed 还是 Aevatar-native。

理由："前端要画一个统一的'能力中心'页面。"

> 判定提示：请分两种用法分别回答 trivial 与否——
> - 情形 1：`source` 字段**只**用于前端展示，运行时仍然按原 `IAgentToolSource` 路径调用工具。
> - 情形 2：`source` 字段**被运行时消费**，用来做路由 / 治理 / approval 分流（例如 NyxID-backed 工具强制走 NyxID approval，Aevatar-native 工具不走）。
>
> 两种情形可能给出不同结论。两者都要答。

### 提案 3：`ITurnContextStore`

新同事在 `Aevatar.AI.Core` 加：

```csharp
public interface ITurnContextStore
{
    Task SaveAsync(string turnId, TurnContext ctx, CancellationToken ct);
    Task<TurnContext?> LoadAsync(string turnId, CancellationToken ct);
}
```

实现是 `InMemoryTurnContextStore : ConcurrentDictionary<string, TurnContext>`。

理由："工具回调里需要恢复 turn 上下文。"

### 提案 4：`AgentProtocolDescriptor`

新同事提议：每个 GAgent 用 `[AgentProtocol(...)]` attribute 声明自己接受的入站事件类型、超时、是否要求 approval、能否被跨 scope 寻址；编译时生成一份 manifest，运行时由 dispatch 层在投递前校验。

```csharp
[AgentProtocol(
    Inbox: [typeof(NeedsLlmReplyEvent), typeof(ApprovalDecidedEvent)],
    CrossScope: false,
    DefaultTimeout: "00:05:00")]
public sealed class AgentRunGAgent : GAgentBase<...> { ... }
```

理由："让 schema-level 治理对每个 GAgent 都成立。"

## 答题区

### 提案 1：`AgentGovernanceProfile`

(a) 是 trivial 抽象。

(b) 它把已有治理结构重新编目：`IAgentTool.ApprovalMode / IsReadOnly / IsDestructive / RequiresApproval(...)`，`ToolApprovalMiddleware` / `YieldApprovalHandler`，以及 `RoleGAgentState.pending_approval` / `PendingToolApprovalState`。#568 §4 原文已经点名：`AgentGovernanceProfile` 只是把已有规则编目，没有产生新约束。

(c) 不适用。它不在 schema / boundary / actor / topology 四层里，也不对应 `Domain / Application / Infrastructure / Host` 的新职责。

(d) 拒绝。按 #568 §4，把规则落回 typed schema、connector boundary、GAgent command handler、topology link；Denied Ledger 只做观察，不做中心 policy。

### 提案 2：`ICapabilityRegistry`

情形 1：`source` 只给前端展示。

(a) 是 trivial 抽象。

(b) 它包装了 `IAgentToolSource.DiscoverToolsAsync(...)`、`IAgentTool.Name / ParametersSchema / IsReadOnly / RequiresApproval(...)`，形态上也接近已有的 `AgentToolVoiceCatalog` / `AgentToolVoiceInvoker` 这种“枚举工具再映射”的 adapter。

(c) 不适用。没有新增运行时约束，只是给 UI 多一层名字。

(d) 拒绝作为运行时 registry；最多改写为 readmodel / view DTO，由现有 tool source fan-out 出“能力中心”展示。

情形 2：`source` 被运行时消费，参与路由 / 治理 / approval 分流。

(a) 不是 trivial 抽象，前提是 `source` 变成强类型边界语义，而不是字符串展示字段。

(b) 不适用。

(c) 新能力是让 NyxID-backed / Aevatar-native 工具走不同 hard boundary 与 approval 路径。落点：Application 层定义窄 `CapabilityDispatchPolicy`/query 契约，Infrastructure 适配 NyxID 或 native provider；治理层属于 #568 §4 的 Boundary-level + Schema-level。

(d) 改写为 typed `CapabilityOrigin` + `CapabilityDispatchPolicy`，由 tool dispatch / connector boundary 消费；不要做全能 `Registry`。

### 提案 3：`ITurnContextStore`

(a) 是 trivial 抽象，而且实现方式违规。

(b) 它把 `RoleGAgentState.sessions`、`PendingToolApprovalState`、`AgentRunGAgentState`、committed event log / projection 这些已有 actor-owned continuation state 包成旁挂 store；`InMemoryTurnContextStore : ConcurrentDictionary<string, TurnContext>` 又退回进程内 run/turn 上下文表。

(c) 不适用。它没有新约束，还违反 #568 §3 “Memory 作为独立 Harness 模块作废”和 `CLAUDE.md` 的中间层状态约束。

(d) 拒绝。工具回调需要恢复上下文时，应把 pending tool / approval / run state 放进对应 actor state，以 reply/timeout event 唤醒继续。

### 提案 4：`AgentProtocolDescriptor`

(a) 不是 trivial 抽象，前提是它真的参与编译期 manifest 和 dispatch hard gate。

(b) 不适用。

(c) 它新增“哪些事件可到达哪个 GAgent、能否跨 scope、默认 timeout / approval 要求”的 schema-level / topology-level 约束，让违规投递无法构造或无法到达。落点：Domain/Application 的 GAgent contract + proto/attribute 元数据，Infrastructure 的 dispatch port 执行校验。

(d) 接受方向，但要改写为 typed proto option / attribute + 生成 manifest + dispatch 校验；禁止变成 `Dictionary<string,string>` metadata 或中心 profile。
