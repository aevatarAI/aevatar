---
title: Lark Reply Chain Completion Semantics
status: Proposed
owner: eanzhao
---

# ADR-0021: Lark Reply Chain Completion Semantics

## Context

Lark reply 主链路 `ConversationGAgent → IChannelLlmReplyRunDispatcher → AgentRunGAgent → ConversationReplyGenerator → ChatRuntime` 跨多个 actor handoff。当前各层完成态语义靠读代码理解：

- dispatcher 同步返回 ≠ user 已 delivered
- committed event ≠ 消费方已收到
- `AgentRunGAgent` terminal ≠ chain 已 finalized
- streaming closeout (`Usage` / `FinishReason` / `IsLast` chunk) 是 round-local 还是 stream-local 无共识

Issues #647 / #648 / #649 都源于这一根本：契约不显式，每层承诺都可能被误读为强保证；后续测试也容易把目标态假设误写成当前既有不变量。

## Decision

### 1. 顶层 4 阶段（chain-level contract）

跨 actor 的 reply chain 完成必须经过四个有序阶段。每个阶段有唯一推进方、唯一可观察 state、明确承诺范围：

| 阶段 | 推进方 | 同步入口 | 异步事件 | 可观察 state | 承诺 |
|---|---|---|---|---|---|
| **accepted** | dispatcher | `DispatchAsync` return | `NeedsLlmReplyEvent` appended | `ConversationState.PendingLlmReply` | 已收到 & 入 actor inbox stream；**不**承诺 LLM 已开始处理 |
| **committed** | `AgentRunGAgent` | — | `AgentRunReplyProducedEvent` persisted | `AgentRunState.Status = REPLY_PRODUCED` | LLM 已产出 & state event 已落库；**不**承诺消费方已收到 |
| **delivered** | `ConversationGAgent` + channel sink | — | `LlmReplyDeliveredEvent` (新增) | `ConversationState.LastReplyDelivery.{outcome, channel_message_id, ack_at_unix_ms}` | channel sink 已 ack；**user-visible** |
| **finalized** | `AgentRunGAgent` + `ConversationGAgent` | — | `ConversationTurnCompletedEvent` + `AgentRunState.cleanup_completed_at != 0` | terminal status + cleanup 已完 | 所有副作用收尾；**吸收态** |

**关键不变量**：

- 任一阶段失败必须转化为 `DROPPED` / `FAILED` 终态，不允许"卡在中间"
- 上一阶段达成是下一阶段的**必要不充分**条件（accepted 后下游可在任何阶段进入 dropped/failed）
- **finalized 是吸收态**：进入后所有 ready / dropped / failed / cleanup signal 必须 no-op；late / stale signal 不改动最终状态

### 2. `AgentRunGAgent` 5 态（actor-internal status）

替代当前 `STARTED / REPLY_PRODUCED / DROPPED / FAILED` 4 态加 `reply_dispatched` bool 的隐式表达：

```proto
enum AgentRunStatus {
  AGENT_RUN_STATUS_UNSPECIFIED = 0;
  STARTED                      = 1;
  REPLY_PRODUCED               = 2;  // = chain.committed
  REPLY_HANDED_OFF             = 3;  // 替代 reply_dispatched bool；LlmReplyReadyEvent 已被 ConversationGAgent 消费
  DROPPED                      = 4;
  FAILED                       = 5;
}
```

**关键消歧**：`AgentRunStatus.REPLY_HANDED_OFF ≠ chain.delivered`。

- `REPLY_HANDED_OFF` = actor-to-actor handoff 完成（`AgentRunGAgent` 视角能观察的最远状态）
- `chain.delivered` = user-visible delivery 完成（由 `ConversationGAgent` 拥有）
- 前者是后者的**必要不充分**条件

`reply_dispatched` bool 字段标记 `reserved`，不物理删除以避免破坏既有 event store。新代码不再读写。

终态判定 helper：

```csharp
public static bool IsTerminal(AgentRunState s) =>
    s.Status == AgentRunStatus.Dropped
 || s.Status == AgentRunStatus.Failed
 || s.Status == AgentRunStatus.ReplyHandedOff;
```

所有 ready / dropped / failed / cleanup handler 入口必须先 `IsTerminal` 短路。

### 3. `ConversationGAgent` 新增 delivery tracking

为把"当前隐式的 lark API 返回值"显式化为 user-visible delivered 信号：

```proto
message ReplyDeliveryStatus {
  oneof outcome {
    Pending pending          = 1;
    Delivered delivered      = 2;
    DeliveryFailed failed    = 3;
  }
  string run_id              = 10;
  message Pending        { int64 started_at_unix_ms = 1; }
  message Delivered      { int64 acked_at_unix_ms   = 1; string channel_message_id = 2; }
  message DeliveryFailed { int64 failed_at_unix_ms  = 1; string error_code = 2; string error_message = 3; }
}

message ConversationState {
  // ... existing fields ...
  ReplyDeliveryStatus last_reply_delivery = N;
}
```

**单字段而非 `map<run_id, ReplyDeliveryStatus>`**：旧 turn 的 delivery 状态对推进 chain 已无意义，多 turn 历史可通过 event log 重建；单字段降低 state size，避免持久化开销线性增长。

落地位置：

- 非 streaming 路径：`ConversationGAgent.RunLlmReplyAsync` (cs:458) 调用 lark API 后 raise event
- streaming 路径：`HandleLlmReplyStreamChunkAsync` (cs:532) / `HandleLlmReplyCardStreamChunkAsync` (cs:546) 在 final chunk emit 之后 raise event
- 失败路径：lark API 4xx/5xx / 超时 → raise `LlmReplyDeliveryFailedEvent`

### 4. Dispatcher 返回值显式化

```csharp
public interface IChannelLlmReplyRunDispatcher
{
    Task<DispatchOutcome> DispatchAsync(NeedsLlmReplyEvent evt, CancellationToken ct = default);
}

public sealed record DispatchOutcome(
    DispatchPhase Phase,
    string CommandId,
    string? RunActorId,
    long AcceptedAtUnixMs);

public enum DispatchPhase
{
    Accepted = 0,
    RejectedStale = 1,        // request age > MaxRunRequestAgeMs
    RejectedDuplicate = 2,    // dedup hit on CommandId
}
```

**`DispatchPhase` 只能取 `Accepted` / `Rejected*`**，**不允许** `Committed` / `Delivered` — dispatcher 不承诺 downstream 任何事。

**兼容性**：接口 `IChannelLlmReplyRunDispatcher` 完全仓库内部消费，无 NuGet 包发布；调用方 3 处（`ConversationGAgent.cs:349` + 2 处测试 mock）随 ADR 适配。无线上兼容性风险。

### 5. 同步 vs 异步承诺矩阵

| 调用点 | 同步返回承诺 | 异步可观察 |
|---|---|---|
| `IChannelLlmReplyRunDispatcher.DispatchAsync` return | `DispatchPhase.Accepted` (仅入 inbox) | `AgentRunReplyProducedEvent` / `AgentRunDroppedEvent` / `AgentRunFailedEvent` |
| `AgentRunGAgent.HandleAgentRunStartRequested` 返回 | run 已接收 | committed → handed_off |
| `ConversationGAgent.HandleLlmReplyReadyAsync` 返回 | handed_off 达成（ack from AgentRunGAgent 视角） | delivered → finalized |
| 对外 HTTP `/v1/messages` 等 | accepted 等价语义 | client 自行 poll readmodel |

**禁止**任何同步调用点承诺"committed"或更强阶段；强保证只能通过异步事件 / readmodel 观察。

### 6. 与 #648 / #649 的桥接

- **#648 streaming closeout contract** = committed→delivered 阶段内部细节。本 ADR 约束：
  - terminal signal **stream-local**（外部消费方只见一次 `IsLast = true` chunk）
  - `Usage` / `FinishReason` 必须挂在最后一个 chunk 上
  - 若 provider 在 `IsLast` 之前先发 `Usage`，runtime **重排**为最后一个 chunk
  - multi-round tool use 的 round-level terminal 不向外暴露
- **#649 terminal idempotency** = finalized 吸收态在 `AgentRunGAgent` 的实现：
  - `IsTerminal` helper 统一收口（替代 cs:114-124 散落的 ad-hoc 判断）
  - 所有 handler 入口先短路终态
  - cleanup 显式幂等（`cleanup_completed_at != 0` 即视为已完成，no-op）
  - stale signal 校验统一通过 `commandId` + `runId` + `MaxRunRequestAgeMs`

## Consequences

### 必要变更

1. `agent_run.proto`：扩 `AgentRunStatus` enum 加 `REPLY_HANDED_OFF`；`reply_dispatched` bool 标记 reserved
2. `conversation_state.proto`：新增 `ReplyDeliveryStatus` 消息 + `last_reply_delivery` 字段
3. 新增 domain event：`LlmReplyDeliveredEvent` / `LlmReplyDeliveryFailedEvent`
4. `IChannelLlmReplyRunDispatcher.DispatchAsync` 改返回 `Task<DispatchOutcome>`；新增 `DispatchOutcome` record + `DispatchPhase` enum
5. `AgentRunGAgent` 把 `Status==ReplyProduced && reply_dispatched==true` 重写为 `Status==REPLY_HANDED_OFF`；新增 `IsTerminal` helper
6. `ConversationGAgent.RunLlmReplyAsync` + streaming chunk path 在 lark API 调用后 raise delivery event 落地 `last_reply_delivery`
7. 配套 `docs/canon/lark-reply-completion-semantics.md`：详细可观察 state 表 + 时序图 + 跨阶段故障矩阵

### 不做

- 不引入显式 `finalized` status 字段 — terminal status (`DROPPED` / `FAILED` / `REPLY_HANDED_OFF`) + `cleanup_completed_at != 0` 组合判定
- 不动 channel sink 内部 retry 策略（属 #649 实现范畴）
- 不引入 multi-turn delivery 历史（用 event log 重建即可）

### 影响面

- `IChannelLlmReplyRunDispatcher.DispatchAsync` 调用方 3 处需适配新返回值（生产 1 + 测试 mock 2），行为不变
- 现有 reply chain 测试约 5+ 个需补 delivery event 断言（评估时认领）
- 后续 #596 run-actor continuation 重构须在此契约下进行（不与本 ADR 冲突）
- event store 兼容：`reply_dispatched` reserved 保留，旧 event 可读

## Open Questions

- multi-channel sink（Lark / Telegram）共享同一 `ReplyDeliveryStatus` 结构 — 默认共享，错误码 `error_code` 字符串承载平台差异
- delivery 失败的 retry 归属：channel sink 内部 retry（建议）vs reply chain 层 retry — 待 #649 实现时定夺
- `cleanup_completed_at` 字段足够 vs 引入独立 `AgentRunCleanupCompletedEvent` — 倾向字段，event 仅在跨 actor 通知时引入

## References

- Issue #647 — 明确并实现 reply chain 的 completion semantics
- Issue #648 — 明确并实现 ConversationReplyGenerator / ChatRuntime 的 closeout contract
- Issue #649 — 强化 AgentRunGAgent 的 terminal 状态幂等性与 stale signal 处理
- 关联：Issue #596 (run-actor continuation) / Issue #560 (StreamSessionGAgent RFC)
