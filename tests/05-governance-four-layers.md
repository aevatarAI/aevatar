# 题 05 — Governance 四层落点

> 满分：10 分
> 必读：
> - Discussion #568 §4 "Governance"（**重写后版本**，loning 的两条 reply 同样是考点）
> - 根目录 `CLAUDE.md` §"Command / Envelope / Dispatch（强制）"
> - 仓库里的工具中间件实现（提示：`grep -rln "IToolCallMiddleware\|ToolApprovalMiddleware" src agents`）

## 题面

Discussion #568 §4（重写版）把 Governance 分为 **四层**：Schema / Boundary / Actor / Topology。每一层分别"让某种违规根本无法构造 / 无法发出 / 无法到达 / 无法越权"。loning 在 reply 里强调："hooks 不是 hard gate"。

### 5.1（4 分）四层映射 — 现实证据

请用下表作答。每一层给出**仓库里一个真实的、当前正在运行的机制**，要求：

- 写出 `<相对路径>::<类型/接口/方法>` 至少一个；
- 给出 **一句 ≤ 25 字** 的解释，说明该机制如何让"违规根本无法构造/发出/到达/越权"。

| 层级 | 当前仓库的对应机制（路径::类型） | 它阻止了什么样的违规 |
|------|------------------------------|--------------------|
| Schema-level | | |
| Boundary-level | | |
| Actor-level | | |
| Topology-level | | |

**禁止**用 `IToolCallMiddleware` 当 Schema 层或 Actor 层的答案——那是 hooks 类（详见 5.2）。

### 5.2（3 分）IToolCallMiddleware 是 hard gate 还是 hook？

> 答题前请先到 [Discussion #568 §4 Governance](https://github.com/aevatarAI/aevatar/discussions/568#discussioncomment-16812382) 找 *"Hooks 的定位（明确分责）"* 那一小段——它**列出了 pre-tool / post-sampling / pre-connector / pre-command-dispatch 等 6 类钩子点**并明确"hard gate 必须在四层结构内"。判断时请以 §4 原文给出的 hook 边界为准，而不是按"调用栈能不能同步阻断"直觉判断。

下列 4 个判断里至少有 2 个是错的，至多有 3 个是对的。请逐条标 `T / F`，并对每条 ≤ 25 字 解释你的依据（必要时引 §4 原文或仓库源码）：

- (i) `IToolCallMiddleware` 在调用栈上同步阻断违规调用，所以是 hard gate。
- (ii) `ToolApprovalMiddleware` + `YieldApprovalHandler` + `PendingToolApprovalState` 把 approval **事件化**进入 actor inbox，这部分语义已经落到 Actor-level，而不仅仅是 hook。
- (iii) 如果未来有人把 `IToolCallMiddleware` 写成"返回 Deny → 直接 throw"，它就成 hard gate 了。
- (iv) Hooks 失败不阻断主流程是设计 bug，应该改为 fail-closed。

### 5.3（3 分）loning 的 "Hooks 跟 GAgent 基类绑定"

loning 的 reply 原文：*"所有的 agents 在定义过程中会定义自己的 harness, 只需要提供继承关系或者 tools 就可以, 不需要有包含语义的 Hooks. 即便是有, 那也是对于某一个 GAgent 基类, 有一些专门的 Hooks、Gates. 也就是说, 这些都应该跟某些有语义的 GAgent 基类绑定."*

请回答：

- (a) 把这条主张翻译成 CLAUDE.md 的语言，应该是哪条原则的延伸？给出 **CLAUDE.md 中的小节标题**。
- (b) 当前 `RoleGAgent` 的工具 / 中间件 / Hook 注册路径**违反或部分违反**了 loning 这条主张吗？给出**一处具体证据**（一行 file::line 或一句源码引用）。如果你认为没有违反，也要给出反证。
- (c) 如果接受 loning 这条主张，第一步最该改的是哪个文件 / 注册点？写出完整路径并给一句改法。

## 答题区

### 5.1

| 层级 | 当前仓库的对应机制（路径::类型） | 它阻止了什么样的违规 |
|------|------------------------------|--------------------|
| Schema-level | `src/Aevatar.AI.Abstractions/ToolProviders/AgentToolBase.cs::AgentToolBase<TParams>.ParametersSchema` | 非法工具参数难构造 |
| Boundary-level | `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/NyxIdRelayAuthValidator.cs::ValidateAsync` | 伪造 relay 回调进不来 |
| Actor-level | `src/Aevatar.AI.Core/RoleGAgent.cs::HandleToolApprovalDecision` | 无 pending 决策不生效 |
| Topology-level | `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorRuntime.cs::LinkAsync` | 未 link 不进父级流 |

证据：

- Schema 层不是 middleware。`AgentToolBase<TParams>` 从参数类型生成 `ParametersSchema`，并在执行前反序列化成 `TParams`；这让工具输入先被工具契约约束。
- Boundary 层在 `NyxIdRelayAuthValidator.ValidateAsync` 中校验 callback JWT、api key、message id、platform、correlation id、body hash 和 replay；失败直接返回认证失败。
- Actor 层在 `RoleGAgent.HandleToolApprovalDecision` 里先对账 `State.PendingApproval` 和 `RequestId`，不匹配直接返回；pending 本身是 `ai_messages.proto` 的 `PendingToolApprovalState`。
- Topology 层在 `OrleansActorRuntime.LinkAsync` 中写入 parent/child 关系并注册 stream hierarchy binding；未初始化 child 会被拒绝。

### 5.2

- (i) **F**。§4 明说 hook 不是 hard gate。
- (ii) **T**。yield 后落 `PendingToolApprovalState`。
- (iii) **F**。throw 仍是 hook 分支。
- (iv) **F**。hook 失败不阻断是设计。

补充判断：

- `IToolCallMiddleware` 在调用栈上能短路，但 §568 §4 的分类看的是结构落点，不看“能不能同步 throw”。
- `ToolApprovalMiddleware` 自身仍是 hook/middleware；真正进入 Actor-level 的部分，是 `YieldApprovalHandler` 让调用 yield，随后 `RoleGAgent` 持久化 `PendingToolApprovalPersistedEvent`、发布 `ToolApprovalRequestEvent`、用 self timeout 事件继续。
- `StreamingToolExecutor` 对 hook 失败有显式吞错注释：`Hook failures must not crash tool execution`。这和 §4 的“hooks 不承担 hard gate”一致。

### 5.3

(a) 对应 `CLAUDE.md` 的 **“Actor 设计原则（强制）”**。更具体地说，是“Actor 即业务实体”的延伸：带语义的 Gates/Hooks 应该跟有语义的 GAgent 基类绑定，而不是挂在全局无语义 hook 链上。

(b) **部分违反**。当前 `AIGAgentBase` 构造函数直接接收通用 `IEnumerable<IToolCallMiddleware>`、`IEnumerable<IAgentRunMiddleware>`、`IEnumerable<ILLMCallMiddleware>`，并在 `RebuildRuntime` 中把 `_toolMiddlewares` 追加进所有工具调用链：

`src/Aevatar.AI.Core/AIGAgentBase.cs:81` 到 `src/Aevatar.AI.Core/AIGAgentBase.cs:96`，以及 `src/Aevatar.AI.Core/AIGAgentBase.cs:310` 到 `src/Aevatar.AI.Core/AIGAgentBase.cs:317`。

这意味着某个 DI 注册的通用 middleware 可以影响所有 `AIGAgentBase` 子类，而不是只绑定到某个语义 GAgent 基类。

(c) 第一步改 `src/Aevatar.AI.Core/AIGAgentBase.cs`。做法：把通用 `IToolCallMiddleware` 只保留为观察/扩展 hook；审批、越权、破坏性工具限制改成 `RoleGAgent` 或更具体语义基类拥有的 typed gate/actor 事件协议，注册点也收敛到该语义基类的构造或专用 factory 中。
