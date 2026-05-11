---
title: feature/lark-bot vs dev Review
status: active
date: 2026-05-11
scope: feature/lark-bot against origin/dev
---

# 2026-05-11 feature/lark-bot vs dev Review

## 范围

- 当前分支：`feature/lark-bot`，HEAD `c2f19698`。
- 对比基线：`origin/dev`，HEAD `36f96f74`。本地 `dev` 落后于 `origin/dev`，所以本轮按 `origin/dev...HEAD` 审查。
- merge-base：`65e48592ca0ba17e3afbc1ec107191dab4319ca3`。
- 改动规模：`156 files changed, 11249 insertions(+), 12318 deletions(-)`。
- 重点路径：NyxID/Lark Channel、Conversation/AgentRun、Identity OAuth、AI ToolProviders、Bootstrap/Mainnet 组合。

## 总体判断

这个分支把 Lark/NyxID 机器人、异步 AgentRun、CardKit streaming、Ornn/NyxID 工具接入都串起来了，方向上是在往统一 Channel Runtime 靠。但当前代码里还有几类架构风险没收住：

- 运行时 credential 和可持久化 domain event 混在同一个 proto 上，已经出现 access token 落入持久事件/state 的路径。
- AgentRun 的输出投递失败会重跑整条 LLM/tool 链，这会重复外部副作用。
- 部分新入口绕过 `IActorDispatchPort`，直接调 actor 或 stream provider，和本仓库的 runtime/dispatch 分责规则不一致。
- mainnet 默认暴露 `ssh_exec` 这类高危工具，虽然工具声明需要 approval，但部署开关语义和注释不一致。

建议合并前至少先处理 F01/F02/F03/F04。其余问题可以分批修，但需要明确 owner 和回归测试。

## Findings

### F01 · P0 · 短生命周期 NyxID access token 会被持久化到 Conversation 事件/state

证据：

- [`NyxIdChatEndpoints.Relay.cs:114`](../../agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Relay.cs:114) 给 inbound `ChatActivity.TransportExtras` 填入 relay 校验得到的 user access token。
- [`chat_activity.proto:276`](../../agents/Aevatar.GAgents.Channel.Abstractions/protos/chat_activity.proto:276) 把 `TransportExtras.nyx_user_access_token` 定义为短生命周期 Nyx user access token。
- [`ChannelConversationTurnRunner.cs:1503`](../../agents/Aevatar.GAgents.NyxidChat/ChannelConversationTurnRunner.cs:1503) 构造 `NeedsLlmReplyEvent` 时直接 `Activity = activity.Clone()`。
- [`ChannelConversationTurnRunner.cs:1533`](../../agents/Aevatar.GAgents.NyxidChat/ChannelConversationTurnRunner.cs:1533) 还会把 sender binding access token 写入 `NeedsLlmReplyEvent.Metadata`。
- [`LLMRequestMetadataKeys.cs:24`](../../src/Aevatar.AI.Abstractions/LLMProviders/LLMRequestMetadataKeys.cs:24) 明确 `SenderNyxIdAccessToken` 是短生命周期 access token。
- [`ConversationGAgent.cs:130`](../../agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.cs:130) 持久化前只清了 `ReplyToken` 和 `ReplyTokenExpiresAtUnixMs`，没有清 `Activity.TransportExtras.NyxUserAccessToken` 和 metadata token。
- [`conversation_state.proto:16`](../../agents/Aevatar.GAgents.Channel.Runtime/protos/conversation_state.proto:16) actor state 会保留 `pending_llm_reply_requests`。

影响：

`reply_token` 被 scrub 掉了，但 `nyx_user_access_token` 和 `nyxid.sender_access_token` 仍会进入 event store、actor state，后续也可能进 projection/read model。这和字段注释里的“short-lived”语义冲突，也违反 secret 不落 durable state 的基本边界。

建议：

- 把运行时 credential 从 `NeedsLlmReplyEvent` 里拆出去，改成 run-command/transient signal 专用字段或 actor 内部 runtime context，禁止进入 `PersistDomainEventAsync`。
- 持久化前统一调用 scrub helper，清理 `Activity.TransportExtras.NyxUserAccessToken` 以及 `nyxid.access_token`、`nyxid.org_token`、`nyxid.sender_access_token` 这类 metadata key。
- 补测试：`ConversationGAgent` 持久化后的 `NeedsLlmReplyEvent` 和 `PendingLlmReplyRequests` 里不能出现 token-like 字段值。

### F02 · P0 · AgentRun 输出投递失败会重跑 LLM/tool，导致外部副作用重复

证据：

- [`AgentRunGAgent.cs:125`](../../agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.cs:125) 在 `ProcessAsync` 外捕获 `AgentRunOutputDispatchException`。
- [`AgentRunGAgent.cs:316`](../../agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.cs:316) 成功路径先 `DispatchReadyEventAsync`，再 `PersistReplyProducedAsync`。
- [`AgentRunGAgent.cs:626`](../../agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.cs:626) 输出投递失败后进入 retry。
- [`AgentRunGAgent.cs:648`](../../agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.cs:648) retry 调度的是新的 `AgentRunStartRequested`，带原始 request clone。
- [`AgentRunGAgent.cs:156`](../../agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.cs:156) 重新处理 start request 会重新进入完整生成流程。
- [`AgentRunGAgentTests.cs:198`](../../test/Aevatar.GAgents.ChannelRuntime.Tests/AgentRunGAgentTests.cs:198) 测试已经把 `replyGenerator.CallCount == 2` 固定成预期行为。

影响：

如果 LLM 已经完成、工具也已执行，只是在向 conversation actor 投递 `LlmReplyReadyEvent` 时短暂失败，当前 retry 会再跑一次 LLM 和工具。工具可能是 Lark 发消息、Ornn 执行、`ssh_exec`、NyxID proxy 等外部副作用，重复执行会造成真实损害，也会重复消耗模型费用。

建议：

- 生成完成后先持久化 `AgentRunReplyProducedEvent` 或 `AgentRunOutputDispatchPendingEvent`，里面保留足够的输出 DTO 和目标 actor。
- retry 只重投已产生的 output，不再调用 reply generator。
- drop notification 也同理，状态和通知投递拆开。
- 改测试：输出投递失败后 retry 成功时，`replyGenerator.CallCount` 应保持为 1。

### F03 · P1 · 新增入口绕过 dispatch port，直接依赖 runtime/stream/actor

证据：

- [`AgentRunDispatcher.cs:14`](../../agents/Aevatar.GAgents.NyxidChat/AgentRunDispatcher.cs:14) 同时依赖 `IActorRuntime` 和 `IStreamProvider`。
- [`AgentRunDispatcher.cs:39`](../../agents/Aevatar.GAgents.NyxidChat/AgentRunDispatcher.cs:39) 自己查找/创建 actor。
- [`AgentRunDispatcher.cs:58`](../../agents/Aevatar.GAgents.NyxidChat/AgentRunDispatcher.cs:58) 直接通过 `_streamProvider.GetStream(actor.Id).ProduceAsync(...)` 投递。
- [`IdentityOAuthEndpoints.cs:206`](../../agents/Aevatar.GAgents.Channel.Identity/Endpoints/IdentityOAuthEndpoints.cs:206) OAuth callback 构造 `CommitBindingCommand` 后直接 `actor.HandleEventAsync`。
- [`IdentityOAuthEndpoints.cs:226`](../../agents/Aevatar.GAgents.Channel.Identity/Endpoints/IdentityOAuthEndpoints.cs:226) broker capability observe 也直接 `clientActor.HandleEventAsync`。
- [`IdentityOAuthEndpoints.cs:700`](../../agents/Aevatar.GAgents.Channel.Identity/Endpoints/IdentityOAuthEndpoints.cs:700) revocation webhook 创建 actor 后直接 `actor.HandleEventAsync`。
- 同文件 rebuild 路径反而已经写明规则并使用 `IActorDispatchPort`：[`IdentityOAuthEndpoints.cs:483`](../../agents/Aevatar.GAgents.Channel.Identity/Endpoints/IdentityOAuthEndpoints.cs:483)。

影响：

这些路径把 lifecycle/topology 和 message dispatch 揉在一起，绕过 dispatch port 可能带来的 inbox 语义、middleware、日志、路由和将来替换 transport 的能力。更麻烦的是，同一个 endpoint 文件里已经存在正确做法，会让后续维护者继续复制不一致模式。

建议：

- `AgentRunDispatcher` 只通过 `IActorDispatchPort.DispatchAsync` 投递；如果需要 actor activation，拆成单独 lifecycle/lookup port。
- OAuth callback、observe、revocation webhook 都改成 dispatch port 投递。
- 加静态门禁：Application/Host/Endpoint 层禁止新增 `actor.HandleEventAsync`，除 runtime/test adapter 外禁止直接使用 stream provider 发送业务 envelope。

### F04 · P1 · mainnet 默认开启 `ssh_exec`，配置语义和注释不一致

证据：

- [`NyxIdToolOptions.cs:20`](../../src/Aevatar.AI.ToolProviders.NyxId/NyxIdToolOptions.cs:20) 注释说明 `ssh_exec` 默认关闭，因为可以执行远程 shell，缺少 approval middleware 的 host 会有风险。
- [`NyxIdSshExecTool.cs:28`](../../src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdSshExecTool.cs:28) 工具名是 `ssh_exec`。
- [`NyxIdSshExecTool.cs:38`](../../src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdSshExecTool.cs:38) approval mode 是 `Auto`，并且 [`NyxIdSshExecTool.cs:41`](../../src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdSshExecTool.cs:41) 永远要求 approval。
- [`MainnetHostBuilderExtensions.cs:110`](../../src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs:110) 注释写“Opt-in”和“flip via config”。
- [`MainnetHostBuilderExtensions.cs:116`](../../src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs:116) 但配置缺失时 `else o.EnableSshExecTool = true`。

影响：

这是高危能力的默认暴露问题。即使工具声明 `RequiresApproval=true`，也不应该把“配置缺失”解释为生产启用。只要 approval 链路有一次组合错误、测试覆盖缺口或调用面绕行，模型就能看到远程命令工具。

建议：

- mainnet 也保持默认 false，只有显式 `Aevatar:NyxId:EnableSshExecTool=true` 才注册。
- 启动期校验：启用 `ssh_exec` 时必须确认 approval handler/middleware 已注册，否则 fail fast。
- 补 mainnet composition 测试：默认不出现 `ssh_exec`；显式开启且 approval 链存在时才出现。

### F05 · P1 · Streaming 完成后的平台清理是 fire-and-forget，异常和顺序都丢了

证据：

- [`ConversationGAgent.cs:443`](../../agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.cs:443) streaming path 完成后进入 housekeeping。
- [`ConversationGAgent.cs:451`](../../agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.cs:451) `_ = ResolveRunner().OnReplyDeliveredAsync(..., CancellationToken.None)` fire-and-forget。

影响：

`OnReplyDeliveredAsync` 里有平台侧状态清理，例如 Lark reaction 从 typing 切到 done。现在它跑在 actor turn 之外，忽略 cancellation，异常无人观察，和后续 actor 消息也没有顺序保证。它虽然不是核心业务状态，但属于用户可见的外部副作用，静默失败会让线上难排查。

建议：

- 如果清理属于 reply 完成语义，就在 actor handler 里 await，并做 bounded timeout/异常日志。
- 如果不想阻塞主 turn，就建模成显式 self-message/internal event，带 `correlation_id` 对账，再由 actor 串行处理和重试。
- 至少用统一 helper 捕获并记录 fire-and-forget 异常，不要裸 `_ =`。

### F06 · P2 · Ornn 技能启用依赖 NyxID 工具注册，但组合契约是隐式的

证据：

- [`ServiceCollectionExtensions.cs:49`](../../src/Aevatar.Bootstrap.Extensions.AI/ServiceCollectionExtensions.cs:49) `EnableOrnnSkills` 是独立开关。
- [`ServiceCollectionExtensions.cs:51`](../../src/Aevatar.Bootstrap.Extensions.AI/ServiceCollectionExtensions.cs:51) 注释说默认 slug 是 `ornn`。
- [`OrnnOptions.cs:7`](../../src/Aevatar.AI.ToolProviders.Ornn/OrnnOptions.cs:7) Ornn provider 自己说默认是 `ornn-api`，并说明 `ornn` 是 SPA 前端。
- [`ServiceCollectionExtensions.cs:894`](../../src/Aevatar.Bootstrap.Extensions.AI/ServiceCollectionExtensions.cs:894) `RegisterOrnnSkills` 只调用 `services.AddOrnnSkills`，没有在当前组合层校验 NyxID client/base URL。
- [`AevatarPlatformHostBuilderExtensions.cs:41`](../../src/workflow/extensions/Aevatar.Workflow.Extensions.Hosting/AevatarPlatformHostBuilderExtensions.cs:41) platform host 默认打开 AI features，并在 [`AevatarPlatformHostBuilderExtensions.cs:48`](../../src/workflow/extensions/Aevatar.Workflow.Extensions.Hosting/AevatarPlatformHostBuilderExtensions.cs:48) 打开 Ornn skills。
- [`src/Aevatar.AI.ToolProviders.Ornn/ServiceCollectionExtensions.cs:17`](../../src/Aevatar.AI.ToolProviders.Ornn/ServiceCollectionExtensions.cs:17) 注释说明调用方必须先注册 `NyxIdApiClient`。

影响：

这个组合靠调用顺序和 DI 失败来暴露问题，但上层 feature flag 看起来是独立能力。slug 默认值的文档也互相打架。结果是某些 host 可以打开 Ornn skills，却没有完整 NyxID proxy 依赖，问题要到启动验证或首次工具调用时才暴露。

建议：

- 统一默认说明为 `ornn-api`。
- 在 AI bootstrap 层增加明确校验：`EnableOrnnSkills=true` 时必须存在 NyxID base URL/client 注册策略。
- 或把开关改名为 `EnableOrnnSkillsViaNyxId`，让依赖关系在配置语义上可见。

### F07 · P2 · CardKit streaming 默认开启，权限不足部署会每轮先失败一次再 fallback

证据：

- [`NyxIdRelayOptions.cs:73`](../../agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/NyxIdRelayOptions.cs:73) CardKit streaming 默认开启。
- [`NyxIdRelayOptions.cs:80`](../../agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/NyxIdRelayOptions.cs:80) 注释说缺 scopes 的部署会 fallback。
- [`ServiceCollectionExtensions.cs:49`](../../agents/Aevatar.GAgents.NyxidChat/ServiceCollectionExtensions.cs:49) 只要 Lark CardKit services 存在，就启用真实 card runner。
- [`ChannelCardConversationTurnRunner.cs:62`](../../agents/Aevatar.GAgents.NyxidChat/ChannelCardConversationTurnRunner.cs:62) 每轮会先调 `card.create`。
- [`ConversationGAgent.LarkCardStreaming.cs:227`](../../agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.LarkCardStreaming.cs:227) 创建失败后才 fallback 到 text-edit。

影响：

默认开启意味着没有 CardKit scope、权限或 quota 的环境，每个 streaming turn 都会先打一次外部 `card.create`，失败后再回落。这样会增加延迟、日志噪声和外部 API 压力，也会把“缺权限”变成正常路径的一部分。

建议：

- 改为部署显式 opt-in，或做 capability probe/cache：遇到明确权限错误后在 TTL 内禁用 card path。
- readiness/health 暴露 CardKit scope 状态，避免上线后通过用户请求探测权限。
- 对 fallback 计数打 metrics，超过阈值自动降级。

### F08 · P2 · runtime-only signal 和 domain-event-shaped proto 混用，靠注释防止持久化

证据：

- [`conversation_events.proto:26`](../../agents/Aevatar.GAgents.Channel.Runtime/protos/conversation_events.proto:26) `NeedsLlmReplyEvent` 同时承担持久事件和 run command 语义，字段注释要求部分 credential 不得持久化。
- [`conversation_events.proto:82`](../../agents/Aevatar.GAgents.Channel.Runtime/protos/conversation_events.proto:82) `LlmReplyStreamChunkEvent` 注释说是 runtime-only signal，绝不能持久化。
- [`conversation_events.proto:108`](../../agents/Aevatar.GAgents.Channel.Runtime/protos/conversation_events.proto:108) `LlmReplyCardStreamChunkEvent` 也是 runtime-only signal。
- [`TurnStreamingReplySink.cs:410`](../../agents/Aevatar.GAgents.Channel.Runtime/TurnStreamingReplySink.cs:410) 这些 runtime signal 仍然被包装进普通 `EventEnvelope`。
- [`ConversationGAgent.cs:529`](../../agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.cs:529) actor 通过普通 `[EventHandler]` 消费。

影响：

这个设计太依赖注释和调用点纪律。只要后续有人把 envelope 统一记录、重放、投影或放进 pending state，就会把 runtime-only signal 当成 domain event。F01 的 token 泄漏已经说明“靠注释保证不落盘”不够硬。

建议：

- 把 command/signal proto 和 committed domain event proto 拆开命名，至少在类型名上表达 `Command` / `Signal` / `CommittedEvent`。
- 给 runtime-only envelope 加 typed dispatch option 或 marker，persistence/projection guard 遇到直接拒绝。
- `NeedsLlmReplyEvent` 拆成 `NeedsLlmReplyCommittedEvent` 和 `AgentRunStartCommand`，不要在同一类型里放“持久”和“只给 run 用”的字段。

### F09 · P3 · 文档/注释与实现不一致，后续维护容易复制错

例子：

- Ornn 默认 slug：AI bootstrap 注释写 `ornn`，provider options 写 `ornn-api`。
- `ssh_exec` mainnet 注释写 opt-in，实际配置缺失时默认 true。
- `NeedsLlmReplyEvent` 注释说 actor 会清 credential，但当前只清了 reply token，没有覆盖 access token。

建议：

把这些注释当成小修一起清掉。这里不是文字洁癖，当前几处注释都在描述安全边界和组合契约，错注释会直接误导下一轮实现。

## 建议补的测试和门禁

- Token scrub 回归测试：持久化的 `NeedsLlmReplyEvent`、`ConversationGAgentState.PendingLlmReplyRequests` 中不允许出现 token 字段。
- AgentRun output retry 测试：output dispatch 第一次失败、第二次成功时，LLM/tool generator 只调用一次。
- Dispatch 边界静态门禁：Endpoint/Application 层禁止新增 `actor.HandleEventAsync`，普通业务路径禁止直接用 `IStreamProvider.ProduceAsync`。
- Mainnet tool composition 测试：`ssh_exec` 默认不注册；显式启用时必须有 approval 链。
- Ornn composition 测试：`EnableOrnnSkills=true` 且缺 NyxID 依赖时启动期 fail fast，错误信息明确。
- CardKit rollout 测试：scope/permission 失败后不应在同一 TTL 内反复尝试 create-card。

## 本轮未做

本轮是静态架构和实现 review，没有运行 `dotnet build` 或测试。报告中的判断基于 `origin/dev...HEAD` diff 和关键路径源码阅读。
