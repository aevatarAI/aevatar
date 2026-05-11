# Aevatar 后端 / 架构入门测验

> 目标读者：会参与 aevatar 后端、架构、平台能力改造的同事
> 答题时长：约 90 分钟
> AI 工具：**允许**（包括 Cursor/Claude Code/ChatGPT/Copilot 等）

如果你是前端同事，只需要掌握基础架构边界，不需要做这 8 道全量题。请做：

- [frontend-architecture-basics.md](./frontend-architecture-basics.md) — 约 40 分钟，关注 Command / Query / ReadModel / Observation / ACK 语义，以及前端不应依赖的后端细节。
- **前端版有自己的精简必读清单**（在该题面顶部），不需要读完下面后端版的必读材料。下面那份是后端 / 架构方向的全量清单。

## 为什么这个测验值得做

允许用 AI 答题，恰恰是因为这套题不奖励"AI 顺手生成的标准答案"。Aevatar 的设计规范、模块边界、与 Discussion #568 之间的差距，**只有把 CLAUDE.md / ADR / 关键源文件读穿才答得对**。AI 帮你检索和组织语言，但代码引用、文件路径、行号、变量名、规则原文必须由你确认到本仓库当前状态。

如果你能在 90 分钟内交出一份证据扎实的答卷，说明你已经具备独立改 aevatar 的最低门槛。

## 必读材料

> 答题前先粗读，答题中按需精读。

仓库内：

- 根目录 [CLAUDE.md](../CLAUDE.md) — 顶级架构约束、Actor / Projection / 命名 / 序列化 / 文档 / 测试 规则
- 根目录 [AGENTS.md](../AGENTS.md) — 配套工程约束（含外部仓库改动权）。如果它和 `CLAUDE.md` 表述不同，以当前分支实际生效的仓库约束为准，答题时写清楚引用的是哪一份。
- [docs/canon/architecture-vocabulary.md](../docs/canon/architecture-vocabulary.md) — Module / Interface / Depth / Seam / Adapter / Leverage / Locality
- [docs/canon/architecture.md](../docs/canon/architecture.md) — 整体分层
- [docs/canon/cqrs-projection.md](../docs/canon/cqrs-projection.md) — 投影主链路
- [docs/canon/role-model.md](../docs/canon/role-model.md) — RoleGAgent / 工具 / 中间件
- [docs/canon/workflow-runtime.md](../docs/canon/workflow-runtime.md) — Workflow run 语义
- [docs/adr/](../docs/adr/) — 全部 ADR；尤其 0006 / 0009 / 0011 / 0013 / 0014 / 0015 / 0019 / 0020

仓库外：

- 讨论 [aevatarAI/aevatar#568 — Aevatar Harness 核心能力边界讨论](https://github.com/aevatarAI/aevatar/discussions/568)
  - §1 Agent Continuation
  - §2 Tools / Skills / Plugins
  - §3 Memory / Session（已作废，作废理由本身是考点）
  - §4 Governance（四层结构 / 反"中心化 Profile"）
  - §5 Multi-Agent（组织架构模型 / TaskBoard 降级）
- 关联 issue [#596 ChatRuntime / ChannelLlmReplyInboxRuntime 收敛](https://github.com/aevatarAI/aevatar/issues/596)
- 关联 issue [#560 SessionStreamGAgent RFC](https://github.com/aevatarAI/aevatar/issues/560)

## 答题方式

后端 / 架构全量题每道题一个 markdown 文件。请在该文件末尾的 `## 答题区` 之下作答，**不要修改题面**。提交时把整个 `tests/` 目录连同改动一起 commit 到你自己的分支。

> 分支命名遵循 `CLAUDE.md` §"提交与 PR" 的硬约束：`<type>/YYYY-MM-DD_<purpose>`，`type ∈ {feat, fix, refactor, docs, test, chore}`。本场答卷固定使用 `docs/` 类型，把 `<your-name>` 换成你自己的英文名（小写、连字符）：

```
git checkout -b docs/2026-05-11_onboarding-exam-<your-name>
git add tests/
git commit -m "docs(tests): <your-name> onboarding exam answers"
git push origin docs/2026-05-11_onboarding-exam-<your-name>
```

如果交卷时已经过了 2026-05-11，请把日期改成你 push 当天的日期，仍然遵循 `YYYY-MM-DD` 定长格式。

题目 8 道，建议时间分配：

| 题号 | 题目 | 建议用时 |
|------|------|---------|
| 01 | 硬规则甄别 — PR 评审 | 15 min |
| 02 | 一次 Lark 回复链路的模块依赖图 | 12 min |
| 03 | Continuation 当前实现 vs #568 理想形态 | 15 min |
| 04 | trivial 抽象判定 | 10 min |
| 05 | Governance 四层落点 | 10 min |
| 06 | 读写分离 + Memory 模块去留 | 12 min |
| 07 | Multi-Agent 组织拓扑现状 | 8 min |
| 08 | 综合论述（≤300 字） | 8 min |

## 评分维度

每题 10 分，总分 80。评分会同时看：

1. **结论正确**：你判断对了吗？
2. **证据扎实**：是否引用了仓库具体文件路径 / 行号 / ADR 编号 / 讨论原文。**没有具体引用的答案最多给一半分。**
3. **规则原文**：能否引到 CLAUDE.md / Discussion #568 的具体段落（不是改写转述）。
4. **当前/理想区分**：能否清晰地把"当前代码这样写"和"目标形态应该这样"分开论述。
5. **避免幻觉**：编造不存在的接口名、文件名、规则会被扣分；先到代码里查。

如果一道题你认为题面有错或前提不成立，可以直接在答题区写 "我认为题面有问题，理由是..."，逻辑自洽且能引到证据，照样给分。

## 出题人友情提示

- 不要直接相信 AI 的第一版答案。它很容易给出"看起来对"的答案：把过时接口名混进来，把已删除的概念当现存，把 #568 已作废的方向当主张。**自己在仓库里 grep 验证一遍再交。**
- 评卷会同步对答案做"反幻觉抽查"——任何引用都会回查到对应文件和行号。
- 题目顺序无依赖。你可以从你最熟的开始。
