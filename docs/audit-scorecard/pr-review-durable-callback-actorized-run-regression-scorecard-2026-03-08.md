# PR Review 架构审计打分（Durable Callback + Actorized Run 回归复核）- 2026-03-08

## 1. 审计范围与输入

1. 审计对象：
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`
   - `src/Aevatar.Foundation.Runtime/Callbacks/RuntimeCallbackEnvelopeFactory.cs`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorRuntime.cs`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Callbacks/OrleansActorRuntimeDurableCallbackScheduler.cs`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/Callbacks/RuntimeCallbackSchedulerGrain.cs`
   - `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs`
   - `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs`
   - `src/workflow/Aevatar.Workflow.Core/Primitives/SubWorkflowOrchestrator.cs`
2. 输入来源：本轮 reviewer 输出的 3 条有效问题（`1 x P1`、`2 x P2`）。
3. 评分口径：`docs/audit-scorecard/README.md`（100 分制、6 维度）。
4. 本文档性质：针对当前 PR diff 的定向 review 打分，不替代全量架构审计文档。

## 2. 审计结论（摘要）

1. delayed runtime retry 进入 durable callback scheduler 后会被重新包装成 self envelope，并把 `PublisherId` 改成当前 actor id；这会破坏依赖原始发布者身份的 handler 语义。
2. Orleans actor 执行 `DestroyAsync` 时只清理 actor grain 与 stream 绑定，没有同时清理同 key 的 durable callback scheduler grain；旧 reminder 仍可能在 actor 重建后向新实例投递陈旧 self-event。
3. script definition query 在 reactivation/recovery 时总是重挂完整 `45s` timeout，而不是按已消耗时间折算剩余 deadline；这会让 pending run 超过配置上限。
4. 当前结论：**74 / 100（B）**，**不建议合并**。原因不是分层形态错误，而是 3 条未关闭的 runtime correctness 回归，其中 `F1` 为阻断项。

## 3. 总体评分（100 分制）

**总分：74 / 100（B）**

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 18 | 分层未被直接打穿，但 runtime callback 与 actor 生命周期的契约边界不完整。 |
| CQRS 与统一投影链路 | 20 | 15 | 事件仍走统一链路，但 delayed retry 会改写 publisher 身份，影响依赖事件来源的业务收敛。 |
| Projection 编排与状态约束 | 20 | 13 | callback scheduler 的事实生命周期没有和 actor teardown 对齐，destroy 后仍可能有旧状态继续发信号。 |
| 读写分离与会话语义 | 15 | 11 | script definition query 的 timeout 语义在 recovery 后被延长，pending run 不能按既定窗口收敛。 |
| 命名语义与冗余清理 | 10 | 8 | `durable callback` 与 actor 销毁语义、timeout 配置语义之间存在实现偏差。 |
| 可验证性（门禁/构建/测试） | 15 | 9 | guards 和定向测试均通过，但没有覆盖这 3 条回归语义。 |

## 4. 问题清单（按严重度）

| ID | 级别 | 问题 | 状态 | 结论 |
|---|---|---|---|---|
| F1 | P1 | delayed runtime retry 重投时丢失原始 `PublisherId` | Open | 阻断合并 |
| F2 | P2 | Orleans actor destroy 未同步清理 durable callback scheduler | Open | 合并前应关闭 |
| F3 | P2 | recovered script definition query 重新挂满额 timeout | Open | 合并前应关闭 |

## 5. 主要扣分项与证据

### F1：delayed runtime retry 重投时丢失原始 `PublisherId`（P1）

1. 直接证据：
   - `RuntimeActorGrain` 在 `AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS > 0` 时，把 `retryEnvelope` 交给 callback scheduler 延迟投递：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs:375`-`:385`
   - Orleans durable callback scheduler 在调度前会调用 `RuntimeCallbackEnvelopeFactory.CreateSelfEnvelope(...)`：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Callbacks/OrleansActorRuntimeDurableCallbackScheduler.cs:26`-`:31`
   - `CreateSelfEnvelope(...)` 会直接把 `PublisherId` 改成当前 `actorId`：`src/Aevatar.Foundation.Runtime/Callbacks/RuntimeCallbackEnvelopeFactory.cs:16`-`:20`
   - Workflow completion 路径明确依赖 `envelope.PublisherId` 判定来源 actor：`src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs:262`-`:286`、`src/workflow/Aevatar.Workflow.Core/Primitives/SubWorkflowOrchestrator.cs:166`-`:176`
2. 影响：
   - child workflow 的 `WorkflowCompletedEvent` 一旦走 delayed retry，重投后 publisher 会从 child actor id 变成 parent/self actor id。
   - `SubWorkflowOrchestrator.TryHandleCompletionAsync(...)` 会把该 completion 视为 publisher mismatch，父 run 无法按预期推进。
3. 扣分归因：
   - 主扣分维度：`CQRS 与统一投影链路`
   - 影响维度：`读写分离与会话语义`

### F2：Orleans actor destroy 未同步清理 durable callback scheduler（P2）

1. 直接证据：
   - `OrleansActorRuntime.DestroyAsync(...)` 当前只清理 parent/child 绑定、调用 `grain.PurgeAsync()`、`grain.DeactivateAsync()` 并移除 stream lifecycle，没有触达 callback scheduler grain：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorRuntime.cs:50`-`:74`
   - durable callback scheduler 使用与 actor 相同的 grain key：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Callbacks/OrleansActorRuntimeDurableCallbackScheduler.cs:86`-`:90`、`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Callbacks/OrleansActorRuntimeDurableCallbackScheduler.cs:100`-`:105`
   - scheduler grain 会把 reminder callback 持久化到 `_state.State.ReminderCallbacks` 并在 reminder 触发时继续投递：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/Callbacks/RuntimeCallbackSchedulerGrain.cs:14`-`:28`、`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/Callbacks/RuntimeCallbackSchedulerGrain.cs:31`-`:68`、`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/Callbacks/RuntimeCallbackSchedulerGrain.cs:105`-`:185`
   - scheduler grain 对外只有按 callback id 取消的接口，没有 destroy-time 的全量 purge 协议：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/Callbacks/IRuntimeCallbackSchedulerGrain.cs:3`-`:16`
2. 影响：
   - workflow/script timeout、runtime retry backoff 等旧 reminder 在 actor 销毁后仍可能 fire。
   - 当显式 actor id 被复用时，新 actor 会收到前一实例留下的陈旧 self-event，破坏 actor 生命周期边界。
3. 扣分归因：
   - 主扣分维度：`Projection 编排与状态约束`
   - 影响维度：`读写分离与会话语义`

### F3：recovered script definition query 重新挂满额 timeout（P2）

1. 直接证据：
   - `ScriptRuntimeGAgent.OnActivateAsync(...)` 会按 `QueuedAtUnixTimeMs` 排序恢复 pending query：`src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:35`-`:40`
   - `QueuedAtUnixTimeMs` 已作为持久态写入 `PendingDefinitionQueries`：`src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:636`-`:647`
   - 但 recovery 路径中的 `ArmPendingDefinitionQueryAsync(...)` 每次都固定调用 `ScheduleSelfDurableTimeoutAsync(..., PendingRunTimeout, ...)`，没有根据 `QueuedAtUnixTimeMs` 计算剩余时间：`src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:367`-`:379`
   - `PendingRunTimeout` 固定为 `45s`：`src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:19`-`:24`
2. 影响：
   - 若 query 已经等待 40 秒后发生 deactivation/reactivation，恢复后又会再拿到新的 45 秒 lease。
   - definition lookup 失败场景的超时上限会被放大，pending run 会显著超出配置语义。
3. 扣分归因：
   - 主扣分维度：`读写分离与会话语义`
   - 影响维度：`可验证性`

## 6. 分模块评分（定向范围）

| 模块 | 评分 | 结论 |
|---|---:|---|
| Foundation Runtime Retry Delivery | 68 | delayed retry 复投破坏原始事件来源语义，影响 sender-sensitive handler。 |
| Orleans Runtime Lifecycle + Callback Scheduler | 70 | actor destroy 与 durable callback state 未形成统一 teardown 边界。 |
| Scripting Runtime Recovery | 76 | recovery 能重建 pending query，但 timeout 语义不再等价于首次排队时的配置窗口。 |
| Guards + Tests | 74 | 当前 guards 与定向测试全部通过，但没有覆盖 publisher 保真、destroy purge、remaining-timeout 三个回归点。 |

## 7. 客观验证记录

1. `bash tools/ci/architecture_guards.sh`
   - 结果：通过。
   - 摘要：`Projection route-mapping guard passed`、`runtime callback guards passed`、`Architecture guards passed`。
   - 结论：现有架构守卫未覆盖本轮 3 条 correctness 回归。
2. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --filter ScriptRuntimeGAgentEventDrivenQueryTests`
   - 结果：通过，`13 passed`。
   - 结论：当前测试覆盖了 replay/recovery 基本流程，但没有校验“恢复后 timeout 必须按剩余时间重挂”。
3. `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --filter OrleansActorRuntimeForwardingTests`
   - 结果：通过，`7 passed`。
   - 结论：当前 destroy 测试只验证 forwarding/stream cleanup，没有验证 callback scheduler purge。
4. 本轮未执行：
   - `dotnet build aevatar.slnx --nologo`
   - `dotnet test aevatar.slnx --nologo`
   - 原因：本报告是基于 reviewer comment 的定向 PR 复核，重点在于闭合具体 correctness 证据链。

## 8. 合并前修复准入标准

1. **F1（Blocking）必须关闭**
   - delayed retry 投递时必须保留原始 `PublisherId`，或者改为在 callback fired envelope 中显式携带原始 publisher 并让消费端读取该事实字段。
   - 必补回归测试：child workflow completion 先失败后 delayed retry，父 run 仍能按 child actor id 正常收敛。
2. **F2 必须关闭**
   - `DestroyAsync(...)` 必须同步 purge 同 actor id 的 durable callback scheduler 状态，避免旧 reminder 向新实例投递。
   - 必补回归测试：destroy 后重建同 id actor，旧 timeout/retry callback 不得再触发。
3. **F3 必须关闭**
   - recovery 时必须按 `QueuedAtUnixTimeMs` 折算 remaining timeout；若剩余时间已耗尽，应立即走 timeout 收敛而不是再挂新 lease。
   - 必补回归测试：query 在接近 45 秒上限时 reactivation，恢复后只使用剩余时间或立即超时。
4. 修复后建议至少执行：
   - `bash tools/ci/architecture_guards.sh`
   - `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --filter ScriptRuntimeGAgentEventDrivenQueryTests`
   - `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --filter OrleansActorRuntime`

## 9. 审计结论

这轮 PR 的问题集中在异步回调与 actor 生命周期交界面，而不是普通实现细节：

1. delayed retry 改写了事件来源身份。
2. actor destroy 没有关闭与其同生共死的 durable callback 事实。
3. script query recovery 拉长了配置超时窗口。

因此，本轮 PR review 定向打分结论为：**74 / 100（B）**，**不建议合并**。建议先关闭 `1 x P1 + 2 x P2`，再做一次带定向回归测试的复评。
