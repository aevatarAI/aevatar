---
title: feature/lark-bot reply-chain test coverage audit
status: active
owner: codex
issue: 634
branch: test/2026-05-12_lark-bot-reply-chain-regressions
---

# `feature/lark-bot` 回复链测试覆盖审计

> 对应 issue: [#634](https://github.com/aevatarAI/aevatar/issues/634)
>
> 目标：不是罗列“这个分支有很多测试”，而是明确回答四件事：
>
> 1. 这条回复链现在的高风险点是什么
> 2. 已有测试到底锁住了哪些不变量
> 3. 还缺哪些关键回归保障
> 4. 后续 `#635 / #636 / #637` 应该先打哪里

## 范围与基线

本次审计以 `feature/lark-bot` 当前链路为基线，重点查看以下六组测试：

- `test/Aevatar.GAgents.ChannelRuntime.Tests/AgentRunGAgentTests.cs`
- `test/Aevatar.GAgents.Channel.Protocol.Tests/ConversationGAgentDedupTests.cs`
- `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelConversationTurnRunnerTests.cs`
- `test/Aevatar.GAgents.ChannelRuntime.Tests/ConversationReplyGeneratorTests.cs`
- `test/Aevatar.GAgents.ChannelRuntime.Tests/TurnStreamingReplySinkTests.cs`
- `test/Aevatar.AI.Tests/ToolCallLoopTests.cs`

另外，以下测试与护栏作为辅助证据使用，用来校正对 `ChatRuntime`、AI 组件边界和结构约束的判断：

- `test/Aevatar.AI.Tests/ChatRuntimeStreamingBufferTests.cs`
- `test/Aevatar.AI.Tests/AIComponentCoverageTests.cs`
- `test/Aevatar.Architecture.Tests/Rules/*`
- `tools/ci/*guard.sh`

并对照以下实现文件确认风险面：

- `agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.cs`
- `agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.LarkCardStreaming.cs`
- `agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.NyxRelayStreaming.cs`
- `agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.cs`
- `agents/Aevatar.GAgents.NyxidChat/ConversationReplyGenerator.cs`
- `agents/Aevatar.GAgents.Channel.Runtime/TurnStreamingReplySink.cs`
- `src/Aevatar.AI.Core/Tools/ToolCallLoop.cs`
- `src/Aevatar.AI.Core/Chat/ChatRuntime.cs`

当前主链路为：

`ConversationGAgent -> IChannelLlmReplyRunDispatcher -> AgentRunDispatcher -> AgentRunGAgent -> ConversationReplyGenerator -> ChatRuntime`

## 总结论

先说结论：这个分支**不是“测试不够”**，而是**测试分布不均**。

已经覆盖得比较强的部分：

- `ConversationGAgent` 的 dedup、retry、reply token strip/re-enrich、streaming/text fallback
- `TurnStreamingReplySink` 的 throttle/cap/finalize/dispatch race
- `ChannelConversationTurnRunner` 的 inbound metadata、reaction、relay/direct reply、card action
- `ToolCallLoop` 的 tool round、middleware、reasoning propagation、length recovery

相对薄弱的部分：

- `ConversationGAgent -> AgentRunGAgent` 之间“accepted / committed / delivered”语义边界是否足够诚实
- `AgentRunGAgent` 的 duplicate terminal signal / repeated callback / stale continuation 组合情形
- `ConversationReplyGenerator` 与 `ToolCallLoop` 的 closeout 联动，而不只是各自单测
- 新引入 seam `IChannelLlmReplyRunDispatcher` 的结构护栏

所以，`#634` 的产出不应该是“建议多补一些 happy path”，而应该是：

1. 承认哪些地方已经很扎实，避免重复造轮子
2. 明确指出后续只需要补少量但高价值的回归
3. 把结构护栏缺口单独拎出来，交给 `#637`

## 风险矩阵

| 风险点 | 现有覆盖 | 结论 | 缺口 / 后续动作 | 本轮优先级 |
| --- | --- | --- | --- | --- |
| `ConversationGAgent` 入站 dedup 与 retry ownership | `ConversationGAgentDedupTests` 覆盖 duplicate activity、duplicate command、retry scheduled / success / exhausted / permanent failure | 覆盖强，主不变量已锁住 | 暂不补功能测试 | P2 |
| relay reply token 不进入 committed fact | `HandleNyxRelayInboundActivityAsync_NeverPersistsReplyTokenIntoEventStore`、`StripsReplyTokenFromPersistedNeedsLlmReplyEvent_ButKeepsItOnRunCommandCopy`、`RehydratesRelayToken...`、`PrefersRunEchoedReplyToken...` | 覆盖强，边界意识明确 | 后续只需补 chain-level 诚实性，不必再补“有没有 strip”基础测试 | P1 |
| `ConversationGAgent` streaming/text/card fallback | `ConversationGAgentDedupTests` 大量覆盖 text chunk、final fallback、card create/stream/finalize | 覆盖强 | 暂不补同类 happy path | P2 |
| `AgentRunDispatcher` 创建 run actor 并发起 start | `DispatchAsync_ShouldCreateRunActorAndDispatchStartCommand` | 基本覆盖到位 | 不缺基础测试 | P2 |
| `AgentRunGAgent` duplicate start / timeout / empty / throw / missing target / missing activity / stale / missing token | `AgentRunGAgentTests` 已覆盖对应单点场景 | 覆盖中上，单点保护不少 | 还缺“terminal 后重复信号”和“组合情形”回归 | P1 |
| `AgentRunGAgent` cleanup 语义 | 已覆盖 schedule cleanup、cleanup destroy | 基础覆盖到位 | 还缺“非 terminal cleanup 请求是否 no-op / 幂等”类测试 | P2 |
| `AgentRunGAgent` streaming ready/card chunk/text-only 路径 | 已覆盖 streaming enabled/disabled、card mode、non-relay path | 覆盖中上 | 可暂缓 | P2 |
| `ChannelConversationTurnRunner` inbound metadata / relay reaction / workflow card action / reply delivery | 覆盖很广，包括 sender binding、owner prefs、reaction clear、interactive relay reply | 覆盖强 | 暂不扩 | P2 |
| `TurnStreamingReplySink` throttle/cap/finalize race | 15 个测试，覆盖 cap、throttle、timer、dispatch in flight、idempotent dispose、duplicate suppression、dispatch throw | 覆盖非常强 | 本轮不需要再做大面积补测，只需补 review 指向的新竞态时再加 | P2 |
| `ConversationReplyGenerator` placeholder / owner vs sender prefs / route fallback / approval middleware | 已覆盖主要分支 | 覆盖中等 | 缺 warning/closeout 联动测试 | P1 |
| `ToolCallLoop` tool round / middleware / request identity / reasoning propagation / length recovery | 覆盖强 | 单体循环语义稳定 | 缺和 generator / runtime 的联动 closeout 测试 | P1 |
| `ChatRuntime` 多轮流式收尾语义 | `ChatRuntimeStreamingBufferTests` 已直接覆盖 `ChatStreamAsync` 的流式 chunk、tool-call follow-up、reasoning 透传；另有 `ToolCallLoopTests` 与 AI tests 辅助覆盖 | 覆盖中等，流式主路径已有直接保护 | `outer stream` 是否应该把 per-round terminal signal 收口成 single overall terminal chunk 目前仍属目标态问题，不是已接受契约；后续应先定契约，再决定是否补 failing test 或实现改动 | P1 |
| 新 seam `IChannelLlmReplyRunDispatcher` 的依赖方向 | 现有 architecture/channel guard 已限制“不要直连 NyxIdChatGAgent”，但没有直接锁住这条 seam | 结构护栏缺口明确 | 在 `#637` 增加 architecture test 或 CI guard | P0 |

## 分文件审计

### 1. `AgentRunGAgentTests`

现有覆盖亮点：

- run actor 创建与 `AgentRunStartRequested` 投递
- duplicate start 幂等
- ready/drop signal not accepted 时的 retry
- unexpected exception / timeout / empty reply / generator throw
- relay token echo、missing token drop、stale request drop
- streaming text / card 路径
- owner LLM config 与 bearer token 透传
- terminal cleanup schedule + destroy

结论：

- 这组测试已经不是“空白区”
- 真正缺的不是单一错误分支，而是**terminal 之后重复信号是否还可能造成二次回传 / 二次调度**

建议补点：

- terminal 状态下再次收到 `AgentRunStartRequested` / cleanup / internal retry 时的 no-op 幂等
- duplicate `LlmReplyReadyEvent` / `Dropped` / `Failed` 对 conversation 端是否仍可能造成二次 closeout

对应后续：

- 归入 `#635`

### 2. `ConversationGAgentDedupTests`

现有覆盖亮点：

- activity / command dedup
- inbound retry 调度归 actor 所有
- reply token strip-on-persist 与 runtime re-enrich
- 运行后 `ReplyToken` 回传优先级
- deferred reply dispatch
- relay streaming chunk / text fallback / card mode fallback
- final edit 与 partial degradation

结论：

- 这是当前分支覆盖最厚的一块之一
- 很多“边界安全”基础测试已经有了，后续不需要再从零补“有没有 strip token”

仍有缺口：

- 更高一层的“accepted / committed / delivered”语义诚实性还没有被单独表达出来
- 例如：conversation 持久化了 `NeedsLlmReplyEvent`，并不等于用户已经收到 reply；目前这类语义主要靠代码结构理解，而不是显式测试名称锁住

对应后续：

- 归入 `#635` 做 chain-level contract test

### 3. `ChannelConversationTurnRunnerTests`

现有覆盖亮点：

- inbound metadata 组装
- owner/sender config layering
- relay typing reaction post / clear
- slash / card action / workflow resume
- direct reply / relay reply / adapter rejection
- `OnReplyDeliveredAsync` 对 streaming path 的 reaction clear

结论：

- 这组测试已经像一个“适配层回归套件”
- 不建议在当前 issue 再扩 happy path

仍有缺口：

- 和 `AgentRunGAgent` 的 end-to-end 语义边界不是在这层表达的
- 所以不应把后续的 actor 语义测试继续塞到 runner tests 里

对应后续：

- 本轮只作为引用基线，不新增任务

### 4. `TurnStreamingReplySinkTests`

现有覆盖亮点：

- interim cap 后 stash，不提前 dispatch
- dispatch 中 stash + throttle gate + deferred timer
- finalize bypass throttle
- finalize in flight wait
- pending == last emitted duplicate suppression
- dispatch throw swallow
- dispose idempotency

结论：

- 这组已经相当扎实，是本链路里并发回归保护最强的一部分
- `#636` 不应该被理解成“这里完全没测”，而是“只补后续 review 指向的新竞态”

仍有缺口：

- 当前没有发现明显大洞
- 若要补，也应只补“新 code path 引入的新 race”，不要做覆盖率型补测
- 本轮新增的两条回归更接近这个方向：一条锁 `FinalizeAsync` 等待 drain 时 `Dispose()` 不应再把 stashed final flush 发出去；一条锁 deferred flush dispatch 失败后，后续 delta 仍可恢复推进且不重复计数

对应后续：

- `#636` 的范围应收窄，避免过度施工

### 5. `ConversationReplyGeneratorTests`

现有覆盖亮点：

- relay callback URL 注入
- placeholder emit / skip
- approval middleware per turn
- owner vs sender preferences layering
- route fallback / no token fallback

结论：

- 配置和偏好层面的覆盖不错
- 但 generator 与 `ToolCallLoop` / `ChatRuntime` 的 closeout 联动还比较少
- 其中 `ChatRuntime` 的 terminal chunk contract 目前还没有稳定成仓库内共识，不适合在这一层直接写死“single overall terminal chunk”断言；这更像后续需要和研发先对齐的目标态

主要缺口：

- `SkillRegistry` 存在但 `IRemoteSkillFetcher` 缺失时的 warning 行为未见直接测试
- 还缺“tool call -> tool result -> final answer”在 generator 这一层只收尾一次的测试表达

对应后续：

- 归入 `#637`

### 6. `ToolCallLoopTests`

现有覆盖亮点：

- no tool / tool then follow-up
- request id 与 per-call metadata
- hook / middleware mutation 与 terminate
- max rounds exhausted
- length recovery
- reasoning content propagation
- DSML tool call 变体

结论：

- `ToolCallLoop` 的单体语义已经很全
- 目前缺的不是 loop 内部逻辑，而是它和 `ConversationReplyGenerator` / `ChatRuntime` 的交界处

主要缺口：

- final answer closeout 与 streaming sink / reply ready 的整体联动没有被直接表达
- “warning path + final content + no duplicate closeout” 这种跨层问题还未集中锁住

对应后续：

- 归入 `#637`

## 已有护栏与缺口

已有护栏：

- `test/Aevatar.Architecture.Tests/Rules/ForbiddenPatternTests.cs`
  - 已限制中间层 `actor/entity/run/session` ID -> context 字典事实态
- `tools/ci/channel_relay_nyx_chat_direct_create_guard.sh`
  - 已限制 channel relay/runtime 直连 `NyxIdChatGAgent`
- `test/Aevatar.Architecture.Tests/Rules/ChannelArchitectureTests.cs`
  - 已限制外部入口绕过 `ConversationGAgent`

新增补强：

- `test/Aevatar.Architecture.Tests/Rules/ChannelArchitectureTests.cs`
  - 可以补一条直接锁 `ConversationGAgent -> IChannelLlmReplyRunDispatcher` seam 的最小门禁
  - 但当前更适合先作为 regex / source-text 级最小护栏看待，不应误认为已经升级为 Roslyn / 编译级依赖门禁

当前缺口：

- 还没有一个专门护栏明确锁住：
  - `ConversationGAgent` 依赖的是 `IChannelLlmReplyRunDispatcher`
  - 而不是重新回退为直接依赖旧 inbox runtime 或具体 NyxIdChat 实现

这说明：

- `#637` 很适合新增一个非常小但高价值的 architecture test / CI guard

## 建议执行顺序

基于这次审计，后续 issue 的执行顺序建议是：

1. `#635`
   - 先补 `ConversationGAgent <-> AgentRunGAgent` 的 chain-level actor 语义与 credential boundary
2. `#637`
   - 再补 closeout 回归和最小结构护栏
3. `#636`
   - 最后只做 review 指向的新 sink 竞态回归，不做大面积补测

原因很简单：

- `TurnStreamingReplySink` 现在不是最薄的位置
- 真正最薄的是“跨 actor handoff 的诚实性”和“closeout 是否只发生一次”

## 结论清单

可以明确认为“已足够，不需要优先再补”的：

- `ConversationGAgent` dedup / retry 基础语义
- relay token strip/re-enrich 基础机制
- `TurnStreamingReplySink` 的大部分并发收尾行为
- `ToolCallLoop` 的单体循环逻辑
- `ChannelConversationTurnRunner` 的大部分 adapter / reaction / relay 分支

可以明确认为“下一步最值得补”的：

- `ConversationGAgent -> AgentRunGAgent` handoff 的 chain-level contract tests
- `AgentRunGAgent` terminal 后重复信号 / cleanup / retry 组合幂等
- `ConversationReplyGenerator` 与 `ToolCallLoop` / `ChatRuntime` 的 closeout 联动
- `IChannelLlmReplyRunDispatcher` 这条新 seam 的结构护栏

一句话总结：

`feature/lark-bot` 现在缺的不是“更多测试”，而是**更少但更准的回归测试与护栏**。
