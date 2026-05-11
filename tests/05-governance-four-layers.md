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
