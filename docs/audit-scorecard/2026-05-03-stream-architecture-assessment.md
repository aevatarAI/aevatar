---
title: Stream Processing Architecture Assessment — toward unified StreamProxyGAgent
status: active
owner: Loning
---

# Stream Processing Architecture Assessment — 2026-05-03

**Audit scope**: full survey of every long-lived stream / bidirectional connection / async chunk pump in the repo.
**Driving question** (Auric, 2026-05-03)：能否用一个统一的 `StreamProxyGAgent` 把所有外部长连接流转换成内部可处理的对象与结构，兼容多种流？
**Method**: 全仓 ripgrep 测绘 SSE writer / IAsyncEnumerable / Channel / WebSocket / WebRTC / IEventSink / IActorEventSubscriptionProvider 的所有命中，逐文件读关键抽象与实现。
**Companion**: 与 [2026-05-03-architecture-audit-detailed.md §3.3](2026-05-03-architecture-audit-detailed.md) 的 "Actor Stream Port" 修复方向直接对应。

> **TL;DR**：`StreamProxyGAgent` 这个方向是**正确的**——仓库里已经独立长出 **3 套近似的 stream 抽象**（AGUI sink / CQRS event sink / VoicePresence transport）+ **9 处不同形态的 ad-hoc 流处理代码**，分散在 5 个层。它们解决的是同一个问题但形状不一致，导致 NyxidChat、Channel reply、Skill runner 反复自造。VoicePresence 已经走出了最完整的"transport ↔ actor"模型，但**缺一步把 session 本身实体化为 actor**。统一的 `StreamProxyGAgent` 应当从 VoicePresence 的形状出发，以 session = actor 为中心，把现有 12 条流路收敛到 1 条。

---

## 1. 现状：12 条流路的全景测绘

按"frame 类型 / 方向 / 生命周期 / 谁拥有事实"分类。

| # | 路径 | 抽象 | Frame 类型 | 方向 | 生命周期 | 当前实现位置 | 是否 Actor 化 |
|---|---|---|---|---|---|---|---|
| 1 | AGUI projection 事件流 | `IAGUIEventSink` + `AGUIEventChannel` | `AGUIEvent`（TextMessage* / ToolCall* / RunFinished…） | out | per-request | `src/Aevatar.Presentation.AGUI/` | 否（host-side bounded Channel）|
| 2 | CQRS projection 事件流 | `IEventSink<TEvent>` + `EventChannel<TEvent>` | 泛型 `TEvent`（实际多用 `EventEnvelope`）| out | per-request | `src/Aevatar.CQRS.Core.Abstractions/Streaming/` | 否（同 #1，但泛型版）|
| 3 | Voice transport（用户侧）| `IVoiceTransport` | `oneof(audio_pcm16, control)` | duplex | long-lived | `src/Aevatar.Foundation.VoicePresence.Abstractions/` | 部分（host bridge + actor dispatch）|
| 4 | Channel 入站适配器 | `IChannelTransport` + `ChannelReader<ChatActivity>` | `ChatActivity` | in | long-lived（按 bot binding）| `agents/Aevatar.GAgents.Channel.Abstractions/Transport/` | 否（host-side adapter）|
| 5 | 通用外部连接 | `IExternalLinkTransport` + `WebSocketTransport` | bytes（无 framing）| duplex | long-lived | `src/Aevatar.Foundation.ExternalLinks.WebSocket/` | 否；文件自带 TODO 列表 |
| 6 | LLM 提供者流 | `ILLMProvider.ChatStreamAsync` | `LLMStreamChunk` | out | per-call | `src/Aevatar.AI.Abstractions/LLMProviders/` | 否（adapter 层 IAsyncEnumerable）|
| 7 | **NyxidChat host bypass** | `IActorEventSubscriptionProvider.SubscribeAsync<EventEnvelope>` + TCS + 120s | `EventEnvelope` | out | per-request | `agents/Aevatar.GAgents.NyxidChat/NyxIdChatStreamingRunner.cs` | **否，且绕过 #1/#2**（VIOLATION，详见本月 audit M1）|
| 8 | Channel reply 节流流 | `TurnStreamingReplySink` | `LlmReplyStreamChunkEvent` | internal | per-turn | `agents/Aevatar.GAgents.Channel.Runtime/` | 半（dispatch 到 actor，但 sink 本身是 host 实例）|
| 9 | Skill runner reply 节流流 | `SkillRunnerStreamingReplySink` | 同 #8 | internal | per-turn | `agents/Aevatar.GAgents.Scheduled/` | 同 #8（**与 #8 平行实现**）|
| 10 | OpenAI realtime session | `IOpenAIRealtimeSession.ReceiveEventsAsync` | `OpenAIRealtimeSessionEvent`（typed oneof）| duplex | long-lived | `src/Aevatar.Foundation.VoicePresence.OpenAI/Internal/` | 否（provider-side WS wrapper）|
| 11 | StreamingTool 执行器 | `StreamingToolExecutor` + 3 × TCS | LLM tool-call frames | internal | per-call | `src/Aevatar.AI.Core/Tools/` | 否 |
| 12 | Channel durable inbox 订阅 | `DurableInboxSubscriber` + per-activity TCS | `ChatActivity` | in | long-lived | `agents/Aevatar.GAgents.Channel.Runtime/Inbox/` | 半（队列工作者，外部 actor 消费）|

**集中观察**：
- **3 套形状几乎完全一致的 sink**（#1 AGUIEventChannel / #2 EventChannel<TEvent> / 隐式：VoicePresence 的 `Channel<VoiceTransportFrame>`）— Push/PushAsync/Complete/ReadAllAsync/有界 Channel/SingleReader=true。它们就是 **同一个抽象在三个 namespace 下复制了三次**。
- **2 套 reply-throttling sink**（#8 #9）几乎逐字平行 — `_dispatchInProgress` / `_pendingText` / `_drainTcs` 形态完全一致。
- **2 个不同的 transport family**：`IVoiceTransport`（duplex, frame-typed）vs `IChannelTransport`（inbound-only, ChatActivity）vs `IExternalLinkTransport`（duplex, untyped bytes）。三套各自演化。
- **没有任何一条路径**让"流本身"成为 actor — 流要么是 host-process Channel，要么是 provider-side wrapper。

---

## 2. 已经收敛得最远的一条路：VoicePresence

VoicePresence 是这次评估里**唯一一条把 transport / dispatch / subscribe 三件事拼起来**的路径。它差一步就到 Auric 想要的形状。

### 2.1 已对的部分

`src/Aevatar.Foundation.VoicePresence.Abstractions/IVoiceTransport.cs`：
```csharp
public interface IVoiceTransport : IAsyncDisposable {
    Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct);
    Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct);
    IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(CancellationToken ct);
}

public readonly record struct VoiceTransportFrame {
    public bool IsAudio { get; init; }
    public ReadOnlyMemory<byte> AudioPcm16 { get; init; }
    public VoiceControlFrame? Control { get; init; }
}
```

✅ **Frame 类型 oneof**（audio | control），未来扩展 video / screen / typed-text 都可以加。
✅ **方向是 duplex**，长连接形态匹配。
✅ **两个 transport 实现**共用同一抽象：`WebSocketVoiceTransport`（PCM 二进制 + JSON 控制）+ `WebRtcVoiceTransport`（RTP/Opus + WebRTC 数据通道）。换 transport 不换 session 逻辑。
✅ **session 通过 actor 事件桥接**（`RemoteActorVoicePresenceSessionResolver`）：
  - 入站 frame → `_dispatchPort.DispatchAsync(actorId, VoiceRemoteAudioInputReceived / VoiceRemoteControlInputReceived)`
  - 出站 frame ← `_subscriptions.SubscribeAsync<VoiceRemoteTransportOutput>(actorId, HandleOutputAsync)`
  - 显式 session id：`VoiceRemoteSessionOpenRequested` / `VoiceRemoteSessionCloseRequested`
  - actor 状态判断后多 session 用 sessionId demux（L218-223 `if (output.SessionId != state.SessionId) return;`）

### 2.2 还差的部分

❌ **session 本身不是 actor**：`RemoteActorVoicePresenceSessionBridge` 用 `Lock _gate` + `AttachmentState? _state` 把 session 状态保存在 host 进程内存。host 节点宕机中途 → session 丢失，transport 重连后没有"接续"概念。
❌ **session 跨节点不可迁移**：actor 在 silo A，bridge 在 host B；如果 actor 重激活到 silo C，subscribe 必须在新节点重建（依赖 `IActorEventSubscriptionProvider` 是否跨 silo，本月 audit 把这一条标为 NEEDS_VALIDATION）。
❌ **frame 类型不可扩展**：`VoiceTransportFrame` 只承载音频和 voice control，加 text / image / tool-approval 必须新设计 transport，无法复用相同 session 逻辑。
❌ **只服务 voice 一个用例**：NyxidChat、AGUI SSE、Channel reply、StreamingProxy room 都没有从这里得到任何复用。

---

## 3. 平行实现造成的代码重复

### 3.1 三个同形 sink

| 类 | 行数 | 接口 | 元素类型 |
|---|---|---|---|
| `AGUIEventChannel` | 68 | `IAGUIEventSink` (Push/PushAsync/Complete/ReadAllAsync) | `AGUIEvent` |
| `EventChannel<TEvent>` | 98 | `IEventSink<TEvent>` (同上) | `TEvent` |
| `WebRtcVoiceTransport._frames` | 内嵌 | `Channel<VoiceTransportFrame>` | `VoiceTransportFrame` |

`AGUIEventChannel` 与 `EventChannel<TEvent>` 完全可以由后者参数化掉。当前是两份代码、两份测试、两份维护负担。

### 3.2 两个同形 reply throttler

| 类 | 行数 | 关键字段 |
|---|---|---|
| `TurnStreamingReplySink` | 359 | `_pendingText` / `_dispatchInProgress` / `_drainTcs` / `_lock` / 节流窗口 |
| `SkillRunnerStreamingReplySink` | 类似 | 同 |

两者解决的都是"LLM 增量 → 节流后 dispatch 到下游 actor"，逻辑完全平行。

### 3.3 三类 transport 没有共同祖先

`IVoiceTransport`（typed duplex frames）/ `IChannelTransport`（typed inbound only + 生命周期）/ `IExternalLinkTransport`（untyped bytes duplex）— 三个 transport 接口，三种 framing 哲学，三套生命周期。

`WebSocketTransport`（#5）的源码注释直接列了 4 条 TODO：no per-message framing / no sub-protocol / no custom headers / 8KB fixed buffer — 这等于自我承认它是临时占位。

### 3.4 NyxidChat 的反复造轮

`NyxIdChatStreamingRunner` (35-74 行) 用 `IActorEventSubscriptionProvider.SubscribeAsync<EventEnvelope>` + `TaskCompletionSource<string>` + `Task.Delay(120_000)` 的形态，**与 VoicePresence 的 RemoteActorVoicePresenceSessionResolver 用同一个 primitive**（`IActorEventSubscriptionProvider`），但形状回退到了 1990 年代 RPC + Timer 的样子，没有用 VoicePresence 已经写好的 session/dispatch 模型。

---

## 4. 命名歧义警告：现有 `StreamingProxyGAgent` ≠ Auric 想要的 `StreamProxyGAgent`

| | 现有 `StreamingProxyGAgent`（agents/Aevatar.GAgents.StreamingProxy/）| Auric 提议的 `StreamProxyGAgent` |
|---|---|---|
| 角色 | 多参与者**群聊房间**消息 broker | 外部长连接流的**统一 transport 适配 actor** |
| 状态 | `Messages[]` `Participants[]` `TerminalSessions{}`（房间事实）| 每个 session 的 frame 序号、对端能力、codec、心跳 |
| 接收 | `GroupChatMessageEvent` `JoinedEvent`（业务事件）| `RawFrame`（来自 transport adapter）|
| 输出 | `Publish` 给所有订阅者的业务消息 | 把内部 `StreamFrame` 写出到外部 wire format |
| 替代 | 不替代任何 transport | 替代 #3/#5/#7/#8/#9 一类的 ad-hoc transport pump |

写 ADR 时务必另起一个名字（`StreamSessionGAgent` / `ExternalStreamGAgent` / `ConnectionGAgent` 都比 `StreamProxyGAgent` 好），否则与现有类碰撞。下文为讨论方便，临时叫 **`SessionStreamGAgent`**。

---

## 5. 建议的统一抽象：`SessionStreamGAgent`

### 5.1 形状

```
                         ┌────────────────────────────────────────┐
                         │         SessionStreamGAgent            │
                         │  (actor; identity = session_id)        │
                         │                                        │
                         │  State (Protobuf):                     │
                         │    - session_id                        │
                         │    - peer_descriptor (channel kind,    │
                         │      negotiated codec, capabilities)   │
                         │    - inbound_seq_high_water_mark       │
                         │    - outbound_seq_counter              │
                         │    - last_acked_outbound               │
                         │    - heartbeat_state                   │
                         │    - lifecycle_status                  │
                         │                                        │
                         │  EventHandlers:                        │
                         │    - InboundFrameReceived(StreamFrame) │
                         │    - OutboundFramePublished(StreamFrame│
                         │    - HeartbeatTimer                    │
                         │    - SessionCloseRequested             │
                         │                                        │
                         │  Publishes (fan-out to consumers):     │
                         │    - StreamFrame (typed)               │
                         └─────┬───────────────────────────┬──────┘
                               │                           │
                  ▲ publish    │ subscribe                 │ subscribe   ▼ publish
       ┌─────────┴──────┐      │                           │      ┌──────┴────────┐
       │ Inbound        │      │                           │      │ Outbound      │
       │ TransportAdapt │      │                           │      │ TransportAdpt │
       │ (host-side)    │      │                           │      │ (host-side)   │
       │ - WS / WebRTC  │      │                           │      │ - WS / WebRTC │
       │ - SSE inbound  │      │                           │      │ - SSE outbound│
       │ - Telegram poll│      │                           │      │ - WHIP        │
       │ - Lark webhook │      │                           │      └───────────────┘
       │ - HTTP upload  │      │                           │
       └────────────────┘      │                           │
                               ▼                           ▼
                        ┌──────────────────────────────────────┐
                        │ Consumer GAgents (业务方)            │
                        │  - ChatGAgent / NyxIdChatGAgent      │
                        │  - VoicePresenceGAgent               │
                        │  - WorkflowRunGAgent (AGUI 等价)     │
                        │  - StreamingProxyGAgent (群聊 broker)│
                        └──────────────────────────────────────┘
```

### 5.2 唯一的 frame 形态

```protobuf
message StreamFrame {
    string session_id = 1;
    int64  seq        = 2;
    google.protobuf.Timestamp ts = 3;

    oneof body {
        TextChunk          text       = 10;   // LLM token / user typing chunk
        ToolCallStart      tool_start = 11;
        ToolCallEnd        tool_end   = 12;
        ToolApprovalPrompt approval   = 13;
        AudioFrame         audio      = 20;   // pcm16 / opus / aac
        VideoFrame         video      = 21;   // future
        ImageFrame         image      = 22;   // multimodal upload
        ScreenFrame        screen     = 23;   // future
        ControlSignal      control    = 30;   // pause / resume / vad / mute
        UserInputCommand   input      = 31;   // typed user input that needs structured handling
        Heartbeat          ping       = 40;
        SessionLifecycle   lifecycle  = 50;   // open / close / error / reconnect
    }
}
```

**关键**：oneof + protobuf 让 transport adapter 不需要懂业务，业务 GAgent 不需要懂 transport。新增模态 = 加一个 oneof 分支，所有 transport 自动透明转发。

### 5.3 谁拥有什么

| 关注点 | 拥有者 |
|---|---|
| Wire 协议解析（HTTP frame / RTP / WS opcode）| Transport Adapter（host-side） |
| Codec 编解码（Opus ↔ PCM16）| Transport Adapter |
| Frame 序号 / 顺序保证 / 重连接续 | **SessionStreamGAgent**（actor 持久态）|
| 对端能力协商 | **SessionStreamGAgent** state |
| 心跳调度 | **SessionStreamGAgent** + `ScheduleSelfDurableTimeoutAsync` |
| 业务 frame 语义（这是 user 输入还是 LLM 输出）| Consumer GAgent |
| Multi-session demux（一个 actor 多个并发 session）| `session_id` 字段 + Consumer GAgent |
| Back-pressure 升级 | SessionStreamGAgent → upstream consumer via control frame |

### 5.4 形状如何吸收现有 12 条流

| 现有路径 | 收敛方案 |
|---|---|
| #1 AGUI sink | 保留 `IAGUIEventSink` 作为 **per-request 出站投影适配**，但其内部由 SessionStreamGAgent 出站流支持；AGUISseWriter 变成 SessionStreamGAgent 的一种 transport adapter（"sse_outbound"）|
| #2 CQRS event sink | 与 #1 合并（已经是同形）；`IEventSink<TEvent>` 保留作为 transport adapter 的内部缓冲，不再是面向应用的契约 |
| #3 IVoiceTransport | 保留为"audio/video transport adapter contract"，但 `VoicePresenceSession` 的 host-side bridge 替换为 SessionStreamGAgent |
| #4 IChannelTransport | 保留 inbound 适配契约，转发 ChatActivity → SessionStreamGAgent（sessionKind=channel_inbound）|
| #5 IExternalLinkTransport / WebSocketTransport | 重写为 SessionStreamGAgent 的 generic WS adapter，把那 4 条 TODO 一次性补完（per-message framing / sub-protocol / headers / dynamic buffer） |
| #6 ILLMProvider.ChatStreamAsync | 保留 provider 内部 IAsyncEnumerable，但 ChatRuntime 把 chunks 包装成 `StreamFrame.text` 推到 actor 出站流 |
| #7 NyxidChat bypass | **删除** `NyxIdChatStreamingRunner`，端点只做 transport adapter → SessionStreamGAgent 的 attach |
| #8 #9 reply throttler | 提取通用 `OutboundFrameThrottler` middleware（actor 内或 transport adapter 内），让 NyxIdChat / Channel.Runtime / SkillRunner 共用 |
| #10 OpenAIRealtime session | provider 的 IAsyncEnumerable 包成 `StreamFrame.audio + StreamFrame.text`，由 VoicePresenceGAgent 转发到 SessionStreamGAgent 出站 |
| #11 StreamingToolExecutor | tool-call 在出站流里就是 `StreamFrame.tool_start/tool_end`，不需要独立 TCS 协调 |
| #12 DurableInboxSubscriber | 保留作为 channel inbox 的 worker；SessionStreamGAgent 不替代 inbox 持久化 |

---

## 6. 与现有架构原则的契合度

| CLAUDE.md 原则 | SessionStreamGAgent 是否符合 |
|---|---|
| 投影编排 Actor 化 | ✅ session = actor，状态在 actor 持久态 |
| Actor 即业务实体（按业务命名）| ✅ "stream session" 是业务实体（用户的一次连接）|
| 短生命周期默认 | ✅ session-scoped，随连接结束 deactivate |
| 跨 actor 等待 continuation 化 | ✅ 没有 host TCS；inbound = command，outbound = subscribed event |
| 单一权威拥有者 | ✅ 每个 session_id 对应唯一 actor，frame seq 单调 |
| Actor 不直接拥有存储 | ✅ session_id 索引 / session 历史走 readmodel |
| 序列化全 Protobuf | ✅ StreamFrame 是 proto |
| 投影端口合规 | ✅ 出站流通过 `IEventSink<StreamFrame>` 喂给 transport adapter，与现有 EventSink 主线一致 |
| 外部仓库无改动权 | ✅ 全部本仓库内做 |

---

## 7. 风险与未解问题

### 7.1 跨 silo session 迁移

如果 host A 接受连接、actor B 在 silo C，frame 路径是 host A → dispatchPort → actor C → publish → subscribeAsync 回 host A。actor C 重激活到 silo D 时，已建立的 transport 连接还在 host A — 这是 VoicePresence 当前没解决的问题，SessionStreamGAgent 也不会因为 actor 化就自动解决。

需要单独设计：
- 短期：transport adapter 在 host 上保持连接，actor 端只持久态；连接所在 host 故障 → client 重连 → 新 host 用 session_id 找回 actor + 拉 last_acked_outbound 后接续。
- 长期：transport 层迁移（QUIC connection migration / WebRTC ICE restart），但这是多季度工作。

### 7.2 出站流回放 vs 实时

如果 client 重连，actor state 里 `outbound_seq_counter` 已到 N，但 client 只收到 N-50：是从 actor state 重放 frame N-49..N，还是丢弃？这影响：
- 出站 frame 是否需要短期持久化（环形 buffer）
- 是 at-least-once 还是 exactly-once
- 多模态音频回放是否合理（音频回放无意义，文本可回放）

建议：text/control 默认可回放（小，便宜），audio/video 默认丢弃，由 `StreamFrame.body` case 决定。

### 7.3 与 Projection Pipeline 的边界

明确：**SessionStreamGAgent 不进 Projection Pipeline 主链**。理由：
- Projection 是 fact-replication 层，承担 long-lived 双向 transport 会让 hub 不再幂等（详见本月 audit §3.3）
- SessionStreamGAgent 的 frame 不一定是 committed domain event（音频帧不该写 event store）
- consumer GAgent 自己决定哪些 frame 要 PersistDomainEventAsync（典型：text 要持久化作为对话历史，audio 不需要）

边界：
- SessionStreamGAgent 的 inbound dispatch → consumer GAgent → consumer 内 PersistDomainEventAsync → CQRS projection → readmodel
- SessionStreamGAgent 的 outbound publish → transport adapter（不进 projection）

### 7.4 AGUI 协议是不是 StreamFrame 的特例

当前 AGUI 已经是一套成熟的 frame 词汇（TextMessageStart / TextMessageContent / ToolCallStart / RunFinished…），而且前端已对接。

两条路：
- (a) **StreamFrame.text/tool_start/tool_end 直接对应 AGUI 子集**，AGUISseWriter 变成 transport adapter。AGUI 的 RunStarted/RunFinished 改为 `StreamFrame.lifecycle`。
- (b) **保留 AGUI 作为前端 wire format**，transport adapter 内部把 StreamFrame ↔ AGUIEvent 做 1:1 映射。

推荐 (b) — 不破坏前端契约。前端继续看 AGUI 字符串，transport adapter 做 framing。

### 7.5 鸡蛋问题：MVP 切片

不要一次性切 12 条流。建议优先级：

| 优先级 | 切片 | 理由 |
|---|---|---|
| P0 | NyxidChat outbound（audit M1）| 是当前确认的 VIOLATION，且形态最简单（纯文本 SSE）|
| P1 | VoicePresence 把 host bridge 替换为 actor | 已有 95% 模板，补 actor 化把 host 单点去掉 |
| P2 | AGUI sink (#1) + EventChannel (#2) 合并 | 纯重构，零业务变化 |
| P3 | TurnStreamingReplySink + SkillRunnerStreamingReplySink 合并为通用 throttler | 重构 |
| P4 | IExternalLinkTransport / WebSocketTransport 重写 | 把 4 条 TODO 收掉，作为通用 WS transport adapter 模板 |
| P5+ | Channel inbound、Realtime session 等 | 长尾 |

每个切片单独 PR + 单独 ADR 增量，不要一个 mega-ADR 一次性吃下。

---

## 8. 评分（流处理子系统专项）

| 维度 | 分 | 说明 |
|---|---|---|
| 抽象一致性 | **3/10** | 3 套 sink + 3 套 transport + 9 处 ad-hoc，无共同祖先 |
| 形状正确性（局部）| **6/10** | VoicePresence 已经接近正确形状；AGUI/CQRS sink 设计合理但缺收敛 |
| Actor 化程度 | **4/10** | 没有任何"流 = actor"的现成实现；最接近的 VoicePresence 也只是 actor + host bridge |
| 多模态扩展性 | **3/10** | 当前要支持 text→audio→video 必须加新 transport 接口；没有统一 frame proto |
| 跨节点鲁棒性 | **3/10** | 所有现有路径都是 host-process Channel，host 宕机 = 流断 = 无接续 |
| 生命周期清晰度 | **6/10** | VoicePresence + IChannelTransport 都有显式 lifecycle；AGUI/CQRS sink 是 per-request OK |
| 性能 / 节流 | **6/10** | 节流逻辑（TurnStreamingReplySink）做得不错，但平行实现 ×2 |
| 与产品 thesis 对齐 | **2/10** | 产品方向是多模态 + realtime，当前抽象只能勉强支撑文本流 |

**专项综合**：**4 / 10**。

> 这个分数不影响 [2026-05-03 主审计](2026-05-03-architecture-audit-detailed.md) 的 6.5/10 综合分（流处理在那份审计的"投影一致性"和"Actor 生命周期"维度里已经计入），这里只做专项度量。

---

## 9. 立即可做的下一步

1. **写 ADR**：`docs/adr/0021-session-stream-gagent.md`，把 §5 的形状、§6 的契合性、§7 的边界写实，列 P0–P3 切片范围。
2. **审查现有 NyxidChat M1 修复方案**：把 audit §3.3 的 "Actor Stream Port" 与本文 §5 的 SessionStreamGAgent 对齐，确认是同一抽象。
3. **预留命名**：把 ADR 标题里的 `SessionStreamGAgent` 锁定（不要再叫 `StreamProxyGAgent`，避免与 `agents/Aevatar.GAgents.StreamingProxy/` 撞名）。
4. **用 VoicePresence 做反向验证**：在 ADR 评审前，先把 `RemoteActorVoicePresenceSessionResolver` 的 host bridge 替换为 actor 的 PoC（不上 PR），看清 cross-silo session 迁移真实代价。
5. **拒绝继续在 NyxidChat 内修小**：禁止在 `NyxIdChatStreamingRunner` 上做任何"小补丁"（加重连、加 idle timeout、加 session id…）— 这些都是在错的地基上加层。所有改动等 SessionStreamGAgent ADR 落地。

---

**Generated by**: arch-audit skill on 2026-05-03 — companion to [the main audit scorecard](2026-05-03-architecture-audit-detailed.md).
