---
title: "定时任务调用凭证的权威来源模型"
status: accepted
owner: eanzhao
---

# ADR-0037: 定时任务调用凭证的权威来源模型

> 跟踪 epic：[#2404](https://github.com/aevatarAI/aevatar/issues/2404) · 来源讨论：[discussion #2402](https://github.com/aevatarAI/aevatar/discussions/2402) · 关联：ADR-0018（per-user NyxID binding / 零 secret material 边界）、ADR-0033（NyxID ephemeral broker，fire 时重签短 token 同范式）、#375（线上零 secret material）

## Context

定时任务触发 service invocation 时，caller 凭证的处理已从历史的 header/payload 透传演进为 typed auth 模型，但当前在 `feature/integrate` 上仍有 **5 条并行路径**，语义重叠、入口能力不一致、且存在一个明确的 secret 持久化 / 安全缺口。

凭证模型当前形态（`ScheduledServiceInvocationAuthState`，`scheduled_dispatch_state.proto:92`）是**三个独立 proto 字段、非 oneof**，互斥只靠注释，类型层面允许非法组合：

- **`SenderNyxId`**（subject + scope，fire 时经 broker 重签短 token）
- **`DurableSenderBearerToken`**（raw bearer token **直接持久化进 actor state**）
- **`ScopeOwnerNyxId`**（owner subject + scope，create/update 时预检）
- 另：**无 auth**、**legacy header/metadata**。

经全仓核实（3 路独立调查）确认的关键事实：

- **没有一条是死代码**：三个 typed 变体各有独立 production 入口（HTTP `/api/schedules`、workflow mapper、Studio provisioning、internal application service）与专项测试，互斥 exactly-one，dispatch 消费优先级 `ScopeOwner > Durable > Sender`。因此本 ADR 是**架构收敛**，不是清理死代码。
- **Sender 与 ScopeOwner 在 broker 层是同一次调用**：`IssueScopeOwnerNyxIdAsync` 转手调 `IssueSenderNyxIdAsync`，`serviceIdentity` 参数被丢弃（`NyxIdScheduledServiceInvocationCredentialExchangePort.cs:74-87`）。两者真实差异仅：subject 来源、fire 时注入字段、create 期是否预检。
- **Durable 是唯一把 raw secret 落进 actor state 的路径**，fire 时走「skip exchange 直接用」分支（`ScheduledServiceInvocationDispatchPort.cs:63-75`）。这违反 ADR-0018 / #375「零长期 secret material」。
- **HTTP 通用入口对 durable 零校验**：`ToAuth()` 仅检查「三选一」，durable 分支无 binding 检查、无 JWT 格式校验，raw 串 trim 后即持久化（`ScheduledDispatchEndpoints.cs:607-621`）。两道 NyxID gate 只对 `ScopeOwnerNyxId` 生效。
- **校验逻辑困在 endpoint 的 private static 方法**：`EnsureScopeOwnerNyxIdBindingExistsAsync` / `EnsureScopeOwnerNyxIdScopeCanBeIssuedAsync` / `ResolveAuthenticatedNyxIdOwnerSubject` 都是 `ScheduledDispatchEndpoints` 私有方法；internal 入口（Studio `CreateAsync` / workflow `EnsureAsync`）**完全绕过**——这是 discussion C「入口能力不一致」的根因：校验没下沉到 application/domain。
- **ScopeOwner 的 create 预检是「真 mint 一次 token」**（调的就是 fire 时同一个 `IssueScopeOwnerNyxIdAsync`），有副作用——正是 discussion B 要消除的反模式。
- **legacy header 路径已基本封堵**：`connector.http.authorization` 已 proto `reserved` + 多处 strip，无残留写 auth 活路径，仅剩防御性 strip 代码。

约束（CLAUDE.md）：**只能改 aevatar**，NyxID / chrono-* 外部仓库无改动权，只能用其既有契约；遵守主链路架构（actor 即业务实体、committed 事件驱动、读写分离、序列化 Protobuf、host 配置注入 FI-002）；接口改动先治理（本 ADR + epic）。

## Decision

把定时任务调用凭证收敛为**单一 typed 权威来源模型**：存「凭证来源 / 引用」，而非可直接使用的 raw token；用 `oneof` 在类型层面保证互斥；统一 Sender 与 ScopeOwner 为一个来源 + `role` 维度；校验下沉到 application/domain 使所有入口语义一致。

1. **统一 NyxID 来源 + role**：`SenderNyxId` 与 `ScopeOwnerNyxId` 合并为一个 `NyxIdCredentialSource(subject, scope, role)`，`role ∈ {SENDER, SCOPE_OWNER}`。role 决定：① subject 解析与归属校验（`SCOPE_OWNER` 必须 = 认证 caller 的 owner subject，不可由 body 伪造；`SENDER` 为目标 subject 但仍需 binding）② fire 时注入目标字段。消除「两个 record 表达同一次 broker 调用」的冗余。
2. **Durable 降级为引用、且 internal-only**：删除新写路径中的「raw bearer token 持久化进 state」；改为 `DurableCredentialReference(credential_id)`，id 是 NyxID agent key 的 handle（Studio minted 本就产出有 id 的 agent key，`ScheduledAgentKeyStudioRunCredentialIssuer.cs:44`）。本结构阶段只落 typed reference，fire 时由 id 经 broker 换短 token 的行为留给后续行为阶段；迁移期旧 raw durable replay 必须 fail closed。**通用 HTTP API 不再接受 durable**——durable reference 只能由 internal trusted provisioning（Studio）写入。
3. **无 auth + required-credential policy**：保留 no-auth，但由 typed policy 按 target/implementation 声明「是否必须带 credential」，避免运行到下游才失败（discussion E）。
4. **校验下沉**：把当前困在 endpoint private static 的 binding/owner 校验下沉到 application/domain 统一路径，HTTP 与 internal 入口走同一套校验语义。
5. **create/update 期只做无副作用 validation**：不再用「真 mint」做预检；改用 binding 存在性（已有的 read-only `bindingQueryPort.ResolveAsync`）+ scope 归属判断；fire/run-now 才签发短 token（具体 introspection 机制见 Open Questions，受限于 NyxID 既有 surface）。
6. **删除 legacy header/metadata auth 残留**：清理已无活写入路径的防御 strip 代码（保留 proto `reserved`）。

Issue #2409 落地的是本 ADR 的 required-credential policy 切片：先采用 schedule-local `IScheduledDispatchCredentialRequirementPolicy`，并把 `credential_requirement_target_kind` 持久化为 schedule actor 的 typed input classification。它不是 allow/deny 决策缓存；未来若引入 governance-backed `ServicePolicySpec.invoke_required_credentials`，新 provider 必须替换当前 schedule-local provider，而不能并存为第二套权威。

`oneof` 草图见 Required Contract。无 opt-in 不变量：收敛后默认凭证语义对既有 schedule 保持等价（迁移期 reducer 双读旧格式），但历史 raw bearer 状态只允许 fail-closed，不允许继续成功调用。

## Locked Rules

1. **类型层互斥**：凭证用 `oneof`，禁止「三个独立字段靠注释互斥」。
2. **存来源/引用，不存 raw token**：actor state / proto / log / readmodel 不落任何可直接使用的 bearer；durable 只存 NyxID agent key 的 id。
3. **来源统一、role 区分**：Sender 与 ScopeOwner 共用一个 `NyxIdCredentialSource`，差异由 `role` 表达；fire 时注入由 role 决定，不为每个 role 复制一条 source 类型。
4. **校验单一路径**：binding/owner/scope 校验属 application/domain，HTTP 与 internal 入口共用；endpoint 不得私藏只对单一变体生效的 gate。
5. **create 无副作用**：create/update 不得通过实际签发 access token 来做校验。
6. **durable internal-only**：通用 HTTP `/api/schedules` 不接受 durable reference；只有 trusted provisioning 路径可写。
7. **owner 不可伪造**：`SCOPE_OWNER` role 的 subject 只能来自认证 principal 的 claim，调用方不能在 body 指定（HTTP 现状已如此，下沉后 internal 入口也须遵守）。
8. **wire-safe 迁移**：旧 tag `1/2/3` 迁移期只标记 deprecated 并双读，不能 reserve；`durable_sender_bearer_token = 2` 与 `legacy_durable_sender_bearer_blocked = 4` 作为只读 fail-closed 哨兵保留。新写只写 `oneof source` 的 tag `5/6/7`，旧 raw bearer 状态必须拒绝调度/dispatch，不得解释为 no-auth。
9. **legacy 删除优先**：无活路径的 legacy auth 代码直接删（FI-007），不留兼容空壳。
10. **读侧诚实**：凭证来源可经 readmodel 暴露（不含 secret）；durable id 与 raw token 都不得投影（沿用现有 `ProjectAsync_ShouldNotProjectDurableSenderBearerToken` 的安全立场）。

## Required Contract（proto 收敛，wire-safe 演进）

```proto
// scheduled_dispatch_state.proto — 目标收敛后
message ScheduledServiceInvocationAuthState {
  ScheduledServiceInvocationNyxIdCredentialSourceState sender_nyx_id = 1 [deprecated = true];
  string durable_sender_bearer_token = 2 [deprecated = true];  // read only; never copy to current state
  ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceState scope_owner_nyx_id = 3 [deprecated = true];
  bool legacy_durable_sender_bearer_blocked = 4;
  oneof source {
    ScheduledServiceInvocationNyxIdCredentialSourceState nyx_id = 5;
    ScheduledServiceInvocationDurableCredentialReferenceState durable = 6;
    ScheduledInvocationAgentKeyCredentialReferenceState scheduled_invocation_agent_key = 7;
  }
  // 未设 source = no-auth（合法性由 required-credential policy 判定）
}

message ScheduledServiceInvocationNyxIdCredentialSourceState {
  ScheduledServiceInvocationNyxIdSubjectRefState subject = 1;  // platform/tenant/external_user_id
  string scope = 2;
  ScheduledServiceInvocationNyxIdCredentialRoleState role = 3;
}

enum ScheduledServiceInvocationNyxIdCredentialRoleState {
  SCHEDULED_SERVICE_INVOCATION_NYX_ID_CREDENTIAL_ROLE_STATE_UNSPECIFIED = 0;
  SCHEDULED_SERVICE_INVOCATION_NYX_ID_CREDENTIAL_ROLE_STATE_SENDER = 1;
  SCHEDULED_SERVICE_INVOCATION_NYX_ID_CREDENTIAL_ROLE_STATE_SCOPE_OWNER = 2;
}

message ScheduledServiceInvocationDurableCredentialReferenceState {
  string credential_id = 1;   // NyxID agent key handle；fire 兑换由后续行为阶段接入
}

message ScheduledInvocationAgentKeyCredentialReferenceState {
  aevatar.credentials.SecretReference secret_reference = 1; // purpose 固定为 scheduled.invocation-agent-key
  string api_key_id = 2;
  int64 key_expires_at_unix_ms = 3;
}
```

应用层 record `ScheduledServiceInvocationAuth`（`ScheduledDispatchModels.cs:46-49`）同步收敛为 `oneof`-等价的判别联合；credential exchange port 收敛为单一 `IssueNyxIdAsync(NyxIdCredentialSource source, ...)`（role 内含），删除 `IssueScopeOwnerNyxIdAsync` 与其被丢弃的 `serviceIdentity` 参数。

新的 scheduled invocation agent key 引用使用 `scheduled_invocation_agent_key = 7`，raw key material 只能通过 vault purpose `scheduled.invocation-agent-key` 存取，state/readmodel/log/API response 只保留 typed reference 与过期时间。

## Entry-Point Alignment（discussion C）

| 入口 | 收敛前 | 收敛后 |
|---|---|---|
| HTTP `/api/schedules` | Sender / Durable(裸奔) / ScopeOwner | NyxId source（role 二选一）；**不接受 durable**；走下沉的统一校验 |
| workflow mapper | Sender / ScopeOwner（类型无 durable） | NyxId source（role 二选一）；与 HTTP 同校验 |
| Studio provisioning | Durable / Sender（从不 ScopeOwner） | NyxId source(SENDER) / DurableReference（trusted-only）；保留 recurring gate |
| internal app service | 全部、绕过所有 gate | 经下沉的统一校验，与 HTTP 等价 |

## Consequences

- 凭证模型从「3 字段 + 注释互斥 + 5 路径」收敛为「1 个 oneof + role + reference」，类型层不可能再出现非法组合或 raw secret 持久化。
- 安全缺口闭合：通用 API 不再能写入任意 bearer；durable 全部走有 id 的 NyxID agent key reference。
- 入口能力一致：Studio 有的保护通用 API 也有，反之亦然；不再「校验只在 HTTP endpoint」。
- 与 ADR-0018 / ADR-0033 / #375 不变量对齐：零长期 secret material；NyxID role source 继续在 fire 时重签短 token，durable reference 的 fire 兑换留到后续行为阶段。
- 成本诚实暴露：proto/接口/多入口/测试均要动，是 epic 级重构（见 Cutover Order）；无副作用 validation 的可达性受限于 NyxID 既有 introspection surface（见 Open Questions）。
- 行为变更点：HTTP 关闭 durable、create 预检不再真 mint——均为**有意行为变更**，需迁移既有正面测试（如 `Create_ShouldAcceptDurableSenderBearerTokenWithoutOwnerBinding`）。

## Cutover Order

分阶段交付，每步 build + 定向测试 + 对应 `tools/ci/*guard*.sh`，详见 epic [#2404](https://github.com/aevatarAI/aevatar/issues/2404)：

1. 接受本 ADR（proposed → accepted）。
2. **Phase 0 契约先行**：proto `oneof` 收敛 + `NyxIdCredentialSource`/role/`DurableCredentialReference`；旧 tag `1/2/3` deprecated 双读，新写只用 `5/6`；proto 重生 + reducer/replay 测试。
3. **Phase 1 校验下沉**：binding/owner/scope 校验从 endpoint private static 下沉 application/domain；HTTP 与 internal 入口对齐；create 改无副作用 validation。
4. **Phase 2 durable 收敛**：HTTP 关闭 durable；raw token → `DurableCredentialReference(id)`；Studio minted 走 reference；旧 raw durable replay fail closed；迁移相关测试。
5. **Phase 3 注入与 legacy 收敛**：fire 时注入由 role 决定；接入 durable reference 的 broker 兑换；核实并收敛 `NyxIdAccessToken`+`NyxIdOrgToken` 双写；`ConnectorHttpAuthorization` 下沉 workflow adapter；删 legacy strip 残留。
6. **Phase 4 policy + 硬化**：required-credential policy；ephemeral-not-for-recurring 在通用 API 也生效；补 discussion 步骤 6 的测试矩阵（create/update validation、run-now、recurring fire、revoked binding、scope mismatch、ephemeral 不允许 recurring）；canon 更新。

## Open Questions（待 Phase 1 评审定稿，不锁定）

- **无副作用 validation 的机制**：依赖 NyxID 是否暴露「检查 scope 可签发但不签发」的 introspection surface。若无，降级为「binding 存在（`ResolveAsync`）+ binding 元数据 scope 匹配」，scope 真实可签发性仍留待首次 fire——本 ADR 不假设 NyxID 新增 surface（外部仓库无改动权）。
- **`NyxIdAccessToken` + `NyxIdOrgToken` 双写是否保留**：需先核实 org token 的下游消费方（schedule 链路外）是否仍依赖同写；确认前不删（discussion D）。
- **`ConnectorHttpAuthorization` 下沉边界**：是否能完全收进 workflow adapter 内部生成，使其退化为纯边界产物。

## Implementation Note

issue-2406 Phase 1 采用无副作用 admission：create/update/ensure 只校验可信 mutation context 的 owner/scope 与 binding readmodel 可见性，不调用 `IssueScopeOwnerNyxIdAsync` 预签 token。由于当前 aevatar 边界内没有 NyxID scope introspection surface，scope 真实可签发性仍保留在 fire-time credential exchange 边界处理。

## Non-Goals

- 改 NyxID（任何形态）。
- per-user 身份穿透（与 ADR-0018 边界一致）。
- 改动 schedule 的 cron/lease/fire 调度机制（与凭证模型正交；re-arm/fire 语义另属其他工作）。
- exactly-once 签发；重签是 fire 时 at-least-once + 短 TTL。

## Outcome

接受并实现后，定时任务调用凭证只有一个权威 typed 来源（`oneof`：NyxId source + role / durable reference），无 raw secret 落 state，所有入口共用同一套下沉校验，通用 API 不再能写入裸 bearer——在 aevatar-only 边界内闭合 discussion #2402 的 5 路径，并与 ADR-0018 / 0033 的零 secret material 范式归一。
