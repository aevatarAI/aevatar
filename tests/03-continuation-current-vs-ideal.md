# 题 03 — Continuation 当前实现 vs Discussion #568 §1 理想形态

> 满分：10 分
> 必读：
> - Discussion #568 §1 "Agent Continuation"（含 loning 的两轮 reply）
> - `src/Aevatar.AI.Core/Chat/ChatRuntime.cs`
> - `src/Aevatar.AI.Core/Tools/ToolCallLoop.cs`
> - `src/Aevatar.AI.Core/RoleGAgent.cs`
> - `agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.cs`
> - 根目录 `CLAUDE.md` §"Actor 执行模型（强制）"

## 题面

Discussion #568 §1 给出一个核心判断："**Agent 在 Aevatar 里的推进，不是 loop，是 actor 内的 event choreography。**" 同时承认当前代码里 `ChatRuntime` / `ToolCallLoop` 仍然是 in-stack loop，是历史包袱。loning 在 §1 reply 中进一步主张 *"我的理解, 不应该有 loop. 即便看起来是 loop, 也是基于事件驱动的"*，并质疑 `ChatRuntime` 这个抽象应当消失。

### 3.1（3 分）找到 loop 的"案发现场"

到 `src/Aevatar.AI.Core/Chat/ChatRuntime.cs` 中：

- (a) 给出一条 `for` 或 `while` 循环的**起始行号**和 1 行 diff-style 引用，证明这就是 §1 描述的 in-stack loop。
- (b) 指出这个 loop 单次循环体内**至少 3 件**带"跨边界 / 长耗时 / 外部副作用"性质的事情（包括但不限于：跨网络调用、跨 actor 投递、HTTP/SSE 流处理、tool 中间件 / hook 调用、channel writer 推送给外部消费者等）。用变量名或方法名引用，不要泛泛说"调 LLM"。
- (c) 用一句话解释：为什么这个 loop 即使包了一层 `async/await`，仍然违反 CLAUDE.md §"Actor 执行模型 — 跨 actor 等待 continuation 化"？

### 3.2（4 分）对照 AgentRunGAgent 是不是 §1 的最小闭环

`AgentRunGAgent`（`agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.cs`）已经是 Issue #596 Phase A 的产物。但它**不等于** Discussion #568 §1 描述的最终形态。

请用以下表格作答（每行 ≤ 25 字）：

| 维度 | §1 理想形态怎么说 | AgentRunGAgent 当前怎么做 | 是否对齐 |
|-----|------------------|--------------------------|--------|
| LLM sampling 是 actor inbox 一拍吗 | | | |
| tool 调用是 actor inbox 一拍吗 | | | |
| approval / timeout 是事件还是循环内特殊分支 | | | |
| 当 actor turn 结束时，下一拍由谁触发 | | | |

最后一行加一句结论（≤ 30 字）：**当前 `AgentRunGAgent` 把多拍折叠成几拍？**

### 3.3（3 分）loning 的 "ChatRuntime 不该存在"

loning 在 §1 第二轮回复中说：*"ChatRuntime 感觉也不该存在. 实际上, 软件工程就是最好的 Harness ..."* 对这个观点：

- (a) 用一句话解释 loning 为什么不接受"短期保留 ChatRuntime 作为 IO worker"这个折中。
- (b) Issue #596 在落地路线上**并没有**完全照搬 loning 的极端方案，给出**两条具体理由**（每条 ≤ 30 字），从 Issue #596 的"Phase A / Phase E / 非目标"段落中找。
- (c) 给出一个判断：**如果今天让你只能改三个文件**把 `ChatRuntime` 的 loop 性质再削弱一档，你会改哪三个文件？每个写一行该改什么。

## 答题区

说明：与 02 一样，`AgentRunGAgent` 相关代码按 `origin/feature/lark-bot` 核对；`ChatRuntime` / `ToolCallLoop` 与当前分支同源，行号也按该基线记录。

### 3.1 loop 的案发现场

(a) `src/Aevatar.AI.Core/Chat/ChatRuntime.cs:260`

```diff
+                    for (var round = 0; round < effectiveMaxToolRounds; round++)
```

(b) 单轮里至少有这些跨边界 / 长耗时 / 副作用：

- `StreamLlmRoundAsync(provider, roundRequest, channel.Writer, ...)`（`ChatRuntime.cs:298-310`）进入 LLM streaming；内部 `provider.ChatStreamAsync(...)` 是外部网络/SSE 流（`ChatRuntime.cs:653`）。
- `channel.Writer.WriteAsync(...)` 把分隔符或 token chunk 推给外部消费者（`ChatRuntime.cs:267-268`、`ChatRuntime.cs:659`）。
- `_hooks.RunPostSamplingAsync(...)` 在 LLM 输出后执行 hook（`ChatRuntime.cs:418`）。
- `streamingExecutor.GetRemainingResultsAsync(...)` 等待工具结果（`ChatRuntime.cs:448`）；工具执行实际进 `MiddlewarePipeline.RunToolCallAsync` 和 `_tools.ExecuteToolCallAsync(...)`（`StreamingToolExecutor.cs:348-360`）。

(c) 它在同一个 actor turn / 调用栈里 `await` LLM、tool、hook 和 writer；而 `CLAUDE.md:110` 要求跨 actor / 外部等待是“发送请求 → 结束当前 turn → reply/timeout event 唤醒继续”。

### 3.2 AgentRunGAgent 与 §1 最小闭环

| 维度 | §1 理想形态怎么说 | AgentRunGAgent 当前怎么做 | 是否对齐 |
|-----|------------------|--------------------------|--------|
| LLM sampling 是 actor inbox 一拍吗 | `LLMSamplingRequested/Completed` | `GenerateReplyAsync` 内部 await | 否 |
| tool 调用是 actor inbox 一拍吗 | `ToolInvocationRequested/Completed` | `ChatRuntime`/executor 栈内等 | 否 |
| approval / timeout 是事件还是循环内特殊分支 | 平等事件唤醒继续 | timeout 是 CTS/catch 分支 | 部分否 |
| 当 actor turn 结束时，下一拍由谁触发 | self-message 或外部事件 | 本 turn 等到终态 | 否 |

结论：当前把 LLM/tool 多拍折进一次 `HandleStartAsync`。

### 3.3 loning 的 “ChatRuntime 不该存在”

(a) loning 不接受这个折中，因为只要还有 `ChatRuntime` 这种中间 orchestrator，就仍是把多维因果压成一根调用栈上的 loop，而不是提示词 + gates + actor event choreography。

(b) Issue #596 没完全照搬极端方案的两个理由：

- Phase A 第一目标是先杀 hosted-service，落 `AgentRunGAgent[runId]`。
- Phase A 明说初期仍可调用 `IConversationReplyGenerator` / `ChatRuntime`；Phase E 才拆它。

(c) 只改三个文件，我会这样削弱 loop：

- `agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.cs`：新增 `SamplingRequested/Completed` self-message handler，让每轮采样回 actor inbox。
- `agents/Aevatar.GAgents.NyxidChat/ConversationReplyGenerator.cs`：把 `GenerateReplyAsync` 拆成单轮 request builder + result mapper，不再自己跑完整多轮。
- `src/Aevatar.AI.Core/Chat/ChatRuntime.cs`：把 `StreamLlmRoundAsync` 提成单轮 IO adapter，外层 `for` 迁出到 actor 事件编排。
