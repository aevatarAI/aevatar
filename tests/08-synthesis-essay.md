# 题 08 — 综合论述（≤ 300 字）

> 满分：10 分
> 必读：
> - 根目录 `CLAUDE.md` §"架构哲学"
> - Discussion #568 主帖 "总体判断" / "外部边界"

## 题面

请用**≤ 300 字**的中文，向一位**刚入职、有通用 agent harness（如 LangChain / OpenAI Assistants / Claude Agent SDK）经验**的工程师，回答：

> **为什么 Discussion #568 主帖说 "Aevatar 不应该简单复刻一个通用 agent harness"？这个不复刻的代价和回报分别是什么？**

要求作答时：

1. 必须**至少**点到下面 4 个底座中的 **3 个**（漏掉 2 个或更多直接 -3 分）：
   - Orleans 上的 GAgent / actor runtime
   - 基于 `EventEnvelope` 和 stream 的 message plane
   - event sourcing 的 actor-owned state
   - `CommittedStateEventPublished` → projection / readmodel 的观察链路
2. 必须**显式指出**通用 agent harness 的什么习惯（典型如 in-stack `while`-loop / 旁挂 vector memory / 中心 policy config）在 Aevatar 这个底座上是**结构性多余**而非 "也能做"。
3. 必须**至少**给出**一个具体的代价**——也就是这条不复刻路线让 aevatar **现阶段做不了什么 / 比通用 harness 慢在哪里**。鼓励诚实回答；这一项给出空泛"成本可控"会扣分。
4. 必须**至少引用**一句 CLAUDE.md 或 Discussion #568 中的原文（≥ 8 个汉字），并标注出处。
5. 不要用项目符号 / bullet / 编号；用连续的散文段落。

> 评分时会按上面 5 条逐一打钩。每漏一条扣 2 分。300 字以内的限制不是建议，是上限：超过 320 字（中文计，不含标点）按超界扣分。

## 答题区
