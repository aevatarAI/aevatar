# PR Review 架构审计打分（Foundation Runtime Callback Scheduler Refactor 回归复核）- 2026-03-07

## 1. 审计范围与输入

1. 审计对象：
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Callbacks/OrleansActorRuntimeDurableCallbackScheduler.cs`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/Callbacks/RuntimeCallbackSchedulerGrain.cs`
   - `src/Aevatar.Foundation.Core/Pipeline/SelfEventEnvelopeFactory.cs`
   - `src/Aevatar.Foundation.Core/GAgentBase.cs`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeEnvelopeRetryPolicy.cs`
   - `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs`
   - `src/Aevatar.Scripting.Abstractions/script_host_messages.proto`
   - `test/Aevatar.Foundation.Runtime.Hosting.Tests/OrleansActorRuntimeCallbackSchedulerTests.cs`
   - `test/Aevatar.Scripting.Core.Tests/Runtime/ScriptRuntimeGAgentEventDrivenQueryTests.cs`
2. 输入来源：本轮 reviewer 输出的 3 条问题（`1 x P1`、`2 x P2`）。
3. 对照基线：`docs/audit-scorecard/pr-review-foundation-runtime-callback-scheduler-refactor-architecture-audit-2026-03-06.md`。
4. 评分口径：`docs/audit-scorecard/README.md`（100 分制、6 维度）。
5. 本文档性质：对上一版“建议合并”结论进行回归复核，不覆盖历史文档。

## 2. 复核结论摘要

1. 上一版 `95/100（A，建议合并）` 的结论本轮不再成立。
2. `durable callback` 默认仍会走 Orleans timer 子路径；该子路径在 grain 重启/失活后不会重建，和“durable”契约直接冲突。
3. runtime auto-retry metadata 被原样继承进 self callback envelope，后续 fired callback 会命中和重试原事件相同的 dedup key，导致 callback 在到达 agent 之前被丢弃。
4. script event-driven definition query 的 pending run 只保存在 `_pendingDefinitionQueries` 内存字典；运行时 reactivation 后，definition response 与 timeout 都会被忽略，run 会永久悬空。
5. 当前结论：**67 / 100（C）**，**不建议合并**。

## 3. 总体评分（100 分制）

**总分：67 / 100（C）**

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 18 | 抽象名为 `durable`，默认实现却回落到不会跨重启恢复的 timer 子路径，契约与实现失配。 |
| CQRS 与统一投影链路 | 20 | 14 | timeout/retry/self-callback 仍走事件化主链，但主链上的关键回调可能在进入 actor 前就丢失或被 dedup 掉。 |
| Projection 编排与状态约束 | 20 | 11 | scheduler timer 注册与 script pending run 被拆成进程内易失状态，跨 activation 事实源不一致。 |
| 读写分离与会话语义 | 15 | 9 | definition query 的异步完成/失败语义无法跨 reactivation 收敛，run 可能永久不提交也不失败。 |
| 命名语义与冗余清理 | 10 | 8 | `durable` 与 `origin_event_id` 的语义边界在当前实现中被污染，self callback 继承了不属于自己的 retry 语义。 |
| 可验证性（门禁/构建/测试） | 15 | 7 | `architecture_guards` 与两组定向测试均通过，但未捕获回归；现有测试还把两处错误语义写成了通过条件。 |

## 4. 问题清单（按严重度）

| ID | 级别 | 问题 | 状态 | 结论 |
|---|---|---|---|---|
| F1 | P1 | durable callback 默认路由到 Orleans timer，重启后不恢复 | Open | 阻断合并 |
| F2 | P2 | self callback 继承 runtime retry metadata，导致 fired callback 被 dedup 丢弃 | Open | 合并前应关闭 |
| F3 | P2 | script definition query pending run 无法跨 runtime reactivation 保留 | Open | 合并前应关闭 |

## 5. 主要扣分项与证据

### F1：durable callback 默认路由到 Orleans timer，重启后不恢复（P1）

1. 直接证据：
   - `ResolveDedicatedDeliveryMode()` 在 `Auto` 模式下会把低于 reminder threshold 的请求路由到 `Timer`：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Callbacks/OrleansActorRuntimeDurableCallbackScheduler.cs:137`-`:147`
   - `RuntimeCallbackSchedulerGrain` 仅在 `_timerCallbacks` 内存字典中保存 timer 注册：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/Callbacks/RuntimeCallbackSchedulerGrain.cs:15`、`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/Callbacks/RuntimeCallbackSchedulerGrain.cs:52`-`:68`、`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/Callbacks/RuntimeCallbackSchedulerGrain.cs:99`-`:115`
   - `OnActivateAsync()` 只初始化 `ReminderCallbacks`，没有重建 timer 注册：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/Callbacks/RuntimeCallbackSchedulerGrain.cs:26`-`:31`
   - `OnDeactivateAsync()` 会直接 `Dispose` 并清空 `_timerCallbacks`：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/Callbacks/RuntimeCallbackSchedulerGrain.cs:33`-`:39`
2. 测试口径证据：
   - 现有单测明确断言 durable timeout 默认使用 `Timer`：`test/Aevatar.Foundation.Runtime.Hosting.Tests/OrleansActorRuntimeCallbackSchedulerTests.cs:16`-`:35`
3. 架构影响：
   - workflow delay/timeout/retry、LLM watchdog、script timeout 等低于 threshold 的 callback 在 silo 重启或 failover 后会永久丢失。
   - 这不是性能优化问题，而是“durable contract 被默认配置破坏”的正确性问题。

### F2：self callback 继承 runtime retry metadata，导致 fired callback 被 dedup 丢弃（P2）

1. 直接证据：
   - `SelfEventEnvelopeFactory.Create()` 会把 inbound envelope 的全部 metadata 原样复制到 self envelope：`src/Aevatar.Foundation.Core/Pipeline/SelfEventEnvelopeFactory.cs:28`-`:31`
   - self durable timeout/timer 调度都会经过该工厂：`src/Aevatar.Foundation.Core/GAgentBase.cs:230`-`:250`、`src/Aevatar.Foundation.Core/GAgentBase.cs:253`-`:276`
   - runtime retry metadata key 定义为 `aevatar.retry.origin_event_id` 与 `aevatar.retry.attempt`：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeEnvelopeRetryPolicy.cs:5`-`:7`
   - `RuntimeActorGrain.BuildDedupKey()` 会优先使用这两个 metadata 构建去重键：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs:137`-`:140`、`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs:403`-`:421`
2. 架构影响：
   - 当 agent 正在处理一个 runtime retry envelope，并在该处理过程中创建 timeout/retry/delay self callback 时，新 callback 会继承同一组 retry metadata。
   - 后续 fired callback 进入 `RuntimeActorGrain` 时会得到与“触发它的重试 envelope”相同的 dedup key，结果在到达 agent 之前就被丢弃。
   - 这使“回调只发信号，业务推进在 actor 内完成”的主链在 retry 场景下失效。

### F3：script definition query pending run 无法跨 runtime reactivation 保留（P2）

1. 直接证据：
   - pending run 仅保存在 `_pendingDefinitionQueries` 进程内字典：`src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:23`
   - `OnActivateAsync()` 不重建 pending run：`src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:34`
   - `OnDeactivateAsync()` 会直接清空 `_pendingDefinitionQueries`：`src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:36`-`:39`
   - definition response 与 timeout callback 都依赖 `TryGetPendingRunContext()` 命中该字典，否则直接忽略：`src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:90`-`:107`、`src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:214`-`:230`、`src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:391`-`:405`
   - `ScriptRuntimeState` 中没有 pending query/timeout lease 的持久字段：`src/Aevatar.Scripting.Abstractions/script_host_messages.proto:54`-`:64`
   - definition response 与 timeout event 只携带 `request_id` / `run_id`，不足以在 reactivation 后重建上下文：`src/Aevatar.Scripting.Abstractions/script_host_messages.proto:227`-`:240`
2. 测试口径证据：
   - 现有单测明确断言 replay 后 `HasPendingDefinitionQuery(...) == false`，并接受 response 被忽略：`test/Aevatar.Scripting.Core.Tests/Runtime/ScriptRuntimeGAgentEventDrivenQueryTests.cs:162`-`:185`
3. 架构影响：
   - event-driven definition-query mode 当前语义不是“volatile runtime”，因为它仍调度 durable timeout 且等待异步 response；但它也不是“durable session”，因为 pending context 不可恢复。
   - 一旦 runtime grain 失活或重启，run 可能既不提交也不失败，破坏 `Command -> Event` 的最终收敛语义。

## 6. 分模块评分（定向范围）

| 模块 | 评分 | 结论 |
|---|---:|---|
| Foundation Runtime + Orleans Callback Scheduler | 58 | durable 抽象被默认 timer 子路径击穿，且缺少 activation/restart correctness 保护。 |
| Foundation Core Pipeline | 63 | self envelope 工厂把 retry 元数据跨语义边界复制，污染了后续 dedup。 |
| Scripting Runtime | 60 | definition query pending run 只保存在易失字典，跨 reactivation 不能收敛。 |
| Guards + Tests | 68 | 现有 guard 全部放行，且两组关键测试口径接受了错误语义。 |

## 7. 客观验证记录

1. `bash tools/ci/architecture_guards.sh`
   - 结果：通过。
   - 解释：当前 guard 未覆盖“durable callback 默认 timer 丢失重启恢复”“retry metadata 泄漏到 self callback”“script pending query reactivation”三类问题。
2. `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --filter OrleansActorRuntimeCallbackSchedulerTests`
   - 结果：通过，`5 passed`。
   - 解释：该组测试当前把“durable timeout 默认走 timer”当作通过条件。
3. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --filter ScriptRuntimeGAgentEventDrivenQueryTests`
   - 结果：通过，`12 passed`。
   - 解释：该组测试当前把“reactivation 后 pending query 不恢复、response 被忽略”当作可接受语义。
4. 本轮未执行：
   - `dotnet build aevatar.slnx --nologo`
   - `dotnet test aevatar.slnx --nologo`
   - 原因：本报告聚焦 review comment 的定向回归复核，主要目标是验证“现有 guards/tests 是否能发现问题”。

## 8. 合并前修复准入标准

1. **F1（Blocking）必须关闭**
   - `durable` 默认路径不得把低于阈值的 callback 落到重启后不可恢复的 timer 子路径。
   - 可接受关闭方式：
     - `Auto` 默认只对 durable 语义使用 reminder；或
     - timer 注册必须持久化并在 `OnActivateAsync()` 完整重建。
   - 必补测试：模拟 deactivation/restart 后，sub-threshold timeout 仍会 fire。
2. **F2 必须关闭**
   - scheduled self callback 必须剥离 `aevatar.retry.origin_event_id` / `aevatar.retry.attempt` 等 runtime retry metadata。
   - 必补测试：在处理 retry envelope 时创建 self timeout/retry callback，后续 fired callback 必须被 agent 接收且只执行一次。
3. **F3 必须关闭**
   - event-driven definition query 的 pending context 必须跨 reactivation 保留，或整体改成一致的显式 volatile 语义并同步删除 durable timeout/异步恢复期待。
   - 在当前 PR 语义下，最直接的关闭标准是：definition response 与 timeout 在 reactivation 后仍能正确提交或失败收敛。
   - 必补测试：`response-after-reactivation` 与 `timeout-after-reactivation` 两条路径。
4. 修复后建议至少执行：
   - `bash tools/ci/architecture_guards.sh`
   - `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --filter Callback`
   - `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --filter ScriptRuntimeGAgentEventDrivenQueryTests`

## 9. 审计结论

这轮 PR 的问题不在“代码风格”或“局部实现瑕疵”，而在于 runtime callback 与 script query 两条异步收敛链路的事实源被拆坏了：

1. durable callback 默认并不 durable。
2. retried envelope 产生的 self callback 会被 dedup 元数据污染。
3. script definition query 在 reactivation 后可能永久悬空。

因此，本轮架构复核结论为：**67 / 100（C）**，**不建议合并**。只有在 1 条 P1 与 2 条 P2 全部关闭，并补齐对应 restart/reactivation/retry regression tests 后，才建议重新复评。
