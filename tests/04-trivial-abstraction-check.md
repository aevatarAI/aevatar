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
