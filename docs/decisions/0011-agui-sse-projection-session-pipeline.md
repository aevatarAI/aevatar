---
title: "AGUI / SSE Projection Session Pipeline"
status: active
owner: liyingpei
---

# ADR-0011: AGUI / SSE Projection Session Pipeline

## Context

Issue #204 收敛的是同一类架构问题：多个用户可见 streaming 入口各自维护一套 host-owned orchestration。

典型问题包括：

- endpoint 直接订阅 raw `EventEnvelope`
- endpoint 在 stream 方法里直接 `CreateAsync(...)` / `HandleEventAsync(...)`
- completion 依赖 `TaskCompletionSource`、`Timer`、`Channel close` 等进程内偶然状态
- AGUI / SSE 映射散落在 Host / endpoint / agent 项目中
- `StreamingProxy` 的 durable completion 尚未收敛到 committed terminal fact + current-state readmodel

这与仓库的顶级架构要求冲突：Host 不能承载核心编排，CQRS 与 AGUI 必须走同一套 Projection Pipeline，查询必须读取 readmodel，不得读取 runtime lease 或 query-time 拼装状态。

## Decision

### 1. 用户可见 streaming 入口统一走 Projection Session Pipeline

以下入口统一回到 interaction service 或等价 projection-session subscription port：

- `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.cs`
- `agents/Aevatar.GAgents.StreamingProxy/StreamingProxyEndpoints.cs`
- `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeGAgentEndpoints.cs`
- `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs`

Host 只负责：

1. 解析 HTTP 请求
2. 调用 command port / subscription port
3. 提供 `emitAsync` 或 SSE writer

Host 不再拥有 observation lifecycle、completion 判定、runtime lease 状态或 raw stream subscription。

### 2. Projection session 分为两类权威键语义

- `command-scoped`: `(RootActorId, SessionId = commandId)`
- `subscription-scoped`: `(RootActorId, SessionId = typed subscriptionId)`

规则：

- AI chat / AGUI 主线默认使用 command-scoped session
- `StreamingProxy room message stream` 使用 subscription-scoped session
- 被动订阅入口不得伪造 `commandId` 充当 `subscriptionId`
- HTTP `scopeId` 是租户/范围语义，不进入 projection session key

### 3. AGUI / SSE 映射属于 projection-owned 组件

`accepted/context`、正文事件、tool 事件、terminal 事件统一由 interaction layer、projector、mapper 或 adapter 发出。

规则：

- Host 不得手搓 `aevatar.run.context` payload
- Host 不得直接把 raw `EventEnvelope` 映射成用户可见 SSE
- typed custom event payload 必须在 abstraction / adapter 边界建模，不得回退成匿名 bag

### 4. StreamingProxy durable completion 必须落到 committed terminal fact

`StreamingProxy room chat stream` 的权威终态链路固定为：

1. `StreamingProxyChatSessionController` 发布 committed terminal event
2. `StreamingProxyChatSessionTerminalProjector` 物化 `StreamingProxyChatSessionTerminalSnapshot`
3. `IStreamingProxyChatSessionTerminalQueryPort` 只读取该 snapshot
4. `StreamingProxyChatDurableCompletionResolver` 只允许用 terminal query port 补齐 durable completion

禁止：

- 从 runtime lease / context / timer 状态推导终态
- 通过 query-time replay 或 query-time priming 补 readmodel
- 以 channel close / detach / callback 线程状态冒充 terminal fact

### 5. current-state readmodel guard 在本设计中是强制项

本 ADR 明确引入：

- `StreamingProxyChatSessionTerminalSnapshot`
- `IStreamingProxyChatSessionTerminalQueryPort`

它们属于 current-state readmodel 与 query path 变更，因此以下 guard 不是“如果碰到了再跑”，而是本设计默认强制门禁：

- `bash tools/ci/query_projection_priming_guard.sh`
- `bash tools/ci/projection_state_version_guard.sh`
- `bash tools/ci/projection_state_mirror_current_state_guard.sh`

任何实现若绕过这些 guard，不满足本 ADR。

## Required Verification

Issue #204 进入实现后，提交前至少执行：

```bash
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
```

若本次同时新增 streaming endpoint guard，也必须附上对应 guard 结果，例如：

```bash
bash tools/ci/streaming_endpoint_guard.sh
```

## Consequences

- `history` 文档继续保留设计快照，但不再单独承担 merge gate 职责
- PR / implementation / review 以本 ADR 的 owner、guard、验收口径为准
- `StreamingProxy` 的 terminal completion 不再允许停留在“先跑通”的 host-owned 临时实现
- 后续补充实现时，若改变 session key、terminal fact、query boundary 或 verification matrix，必须同步更新本 ADR

## Related

- [Issue 204：统一 AGUI / SSE 到 Projection Session Pipeline 技术设计](../history/2026-04/2026-04-17-issue-204-agui-sse-projection-session-design.md)
