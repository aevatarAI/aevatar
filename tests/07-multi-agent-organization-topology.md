# 题 07 — Multi-Agent 组织拓扑现状

> 满分：10 分
> 必读：
> - Discussion #568 §5 "Multi-Agent"（**重写后版本**，含 loning 的"按公司组织架构设计"reply 串）
> - [docs/adr/0006-multi-agent-evolution.md](../docs/adr/0006-multi-agent-evolution.md)
> - `src/Aevatar.Foundation.Core/MultiAgent/TaskBoardGAgent.cs`
> - `src/Aevatar.Foundation.Core/MultiAgent/TeamManagerGAgent.cs`
> - `test/Aevatar.Foundation.Core.Tests/MultiAgent/*`

## 题面

Discussion #568 §5（重写版）的核心结论：

1. Aevatar 平台层只需要 **3 个 primitive**：GAgent、parent/child link、消息。
2. `TeamManagerGAgent` / `TaskBoardGAgent` **不再是平台 primitive**，应"退回 application-level example pattern"——具体到代码上 §5 写过一行明确建议：*"`TaskBoardGAgent` → 同上，作为'分发部门'的具体实现示例 ... 移到 `examples/` 或 `samples/`，命名改为业务名"*。

但如果你 `find . -name "TaskBoardGAgent.cs"`，会发现它**当前还住在** `src/Aevatar.Foundation.Core/MultiAgent/` ——这是平台基础层，不是 examples。

### 7.1（4 分）现状盘点

- (a) 用 1 行 `find` 或 `rg` 命令证明 `TaskBoardGAgent` 当前的位置。把命令和**输出第一行**贴在答题区。
- (b) `Aevatar.Foundation.Core` 这个项目在 CLAUDE.md / `docs/canon/architecture-vocabulary.md` 的语境里属于哪一层？请引一句原文。
- (c) 把 `TaskBoardGAgent` 留在 `Foundation.Core` 而不挪到 `examples/`，与 §5 重写后的结论之间存在**几条矛盾**？逐条列出（不少于 2 条），每条 ≤ 25 字。

### 7.2（3 分）平台 primitive 三件套是否当前已足够

§5 主张：**"GAgent + parent/child link + 消息"** 已经够用，多 worker / reviewer / 任务分发都是这三件套的运行时编排。

- (a) 在仓库里**至少给出 1 个**（最好 2 个）当前已经实现的、能证明 "parent/child link" 是平台一等概念的 API 入口（写出 `路径::方法名`）。如果给出 2 个，请说明它们是**抽象 + 实现**（interface ↔ impl）的一对，还是**两个独立入口**。
- (b) loning 的反问：*"我通过 workflow 已经明确定义了我处理什么消息, 我如何处理. 那我等着消息来就可以了. 然后有 StreamProxy 把外部的事件传进传出系统. 我为什么还要关心去哪里拿任务?"* — 用一句话翻译这条反问对应到 CLAUDE.md 中**哪条强制约束**（§"Actor 设计原则" 或 §"Actor 执行模型" 里的某条）。
- (c) **反方向**：列出**一个**仅靠 "GAgent + parent/child + 消息" **不容易表达**的需求，并简述需要补什么（典型例子可在 §5 "主要缺口" 段找到）。

### 7.3（3 分）StreamProxy 的命名歧义

Issue #560 提示了一个命名警告：

> Auric 提到的 `StreamProxyGAgent` **不等于**现存的 `agents/Aevatar.GAgents.StreamingProxy/StreamingProxyGAgent.cs`（那是群聊房间 broker，做 `Messages[] / Participants[]` 状态机）。

请回答：

- (a) 当前 `StreamingProxyGAgent` 的**真实职责**是什么？用一句 ≤ 30 字 的话概括（先打开它的 State 类型看字段）。
- (b) Issue #560 RFC 里讨论的 `SessionStreamGAgent` 真正想抽象的是什么？用一句 ≤ 30 字 的话概括。
- (c) §5 的 loning reply 中提到："*StreamProxyGAgent 未来支持多模态的话, 那么 RoleGAgent 现在不能处理多模态. 整个体系感觉是要用策略模式之类的.*" — 把这条话转写成：**当前 `RoleGAgent` 的输入 / 输出契约面向多模态需要补什么**？给一个具体改造点（一句话即可，可以是新 proto field、新 message、新 sub-state）。

## 答题区
