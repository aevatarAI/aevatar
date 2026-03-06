# Volatile Runtime Callback Guard And Test Checklist - 2026-03-06

## 1. 目标

1. 将 `volatile runtime` 语义落成可执行门禁，而不是停留在蓝图说明。
2. 防止 callback 主链回退到第二套延迟路径、弱 metadata 校验、或把运行态重新塞进 event-sourced state。
3. 为后续代码重构提供明确的完成标准。

## 2. 适用边界

1. `scheduler` correctness 仍必须保证：
   - `lease` 取消正确
   - `generation` CAS 正确
   - fired metadata 完整
2. `business actor runtime coordination` 采用“事实态持久 + lease activation-local”分层：
   - workflow actor 的 timeout/retry/pending wait/session 仍不持久化到 business state
   - script definition query 的 `pending request` 允许持久化到 `ScriptRuntimeState`
   - callback lease/backend/generation 仍必须保持 activation-local，晚到 callback 通过 lease 对账后 `no-op`
3. 本清单覆盖范围：
   - `src/Aevatar.Foundation.Runtime*`
   - `src/workflow/Aevatar.Workflow.Core/*`
   - `src/Aevatar.Scripting.Core/*`
   - `tools/ci/*`
   - 对应 `test/*`

## 3. 必须新增的 Guard

### G1 runtime callback 主链 guard

1. 新增脚本：`tools/ci/runtime_callback_guards.sh`
2. 必查规则：
   - 禁止在以下区域出现 `Task.Delay(` 作为业务重试/超时调度路径：
     - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`
     - `src/workflow/Aevatar.Workflow.Core/Modules/*`
     - `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs`
   - 禁止在上述区域出现 `Task.Run(` 直接推进 callback 业务分支。
   - 允许的例外仅限 scheduler 基础设施内部循环：
     - `src/Aevatar.Foundation.Runtime/Callbacks/InMemoryActorRuntimeCallbackScheduler.cs`
3. 失败信息必须明确指出：
   - 是哪条文件
   - 命中了哪种禁用模式
   - 为什么违反“callback 主链唯一”

### G2 strict callback metadata guard

1. 目标：workflow/script 代码中不得再手写“只读 generation”的弱校验。
2. 必查规则：
   - `src/workflow/Aevatar.Workflow.Core/Modules/*`
   - `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs`
   内部不得直接以 `RuntimeCallbackMetadataKeys.CallbackGeneration` 作为唯一判据推进业务。
3. 推荐门禁方式：
   - 禁止模块代码直接读取 `RuntimeCallbackMetadataKeys.CallbackGeneration`
   - 统一要求通过共享校验器，例如 `RuntimeCallbackEnvelopeMetadataReader.MatchesLease(...)`
4. 允许例外：
   - Foundation runtime scheduler 自身在“写 metadata”时可直接使用 key 常量

### G3 runtime state boundary guard

1. 目标：防止 activation-local callback lease/runtime backend 等运行态重新写进 business actor state，同时允许 script definition query 的 pending fact 可恢复。
2. 必查规则：
   - `src/workflow/Aevatar.Workflow.Core/workflow_state.proto` 不得新增 callback runtime coordination 字段：
     - `timeout_lease`
     - `retry_backoff`
     - `pending_delays`
     - `pending_signals`
     - `pending_llm_calls`
     - `runtime_runs`
   - `src/Aevatar.Scripting.Abstractions/script_host_messages.proto` 允许新增 `pending_definition_queries` / `PendingScriptDefinitionQueryState`
   - 但不得新增 activation-local lease 字段：
     - `timeout_generation`
     - `timeout_backend`
     - `timeout_lease`
   - `ScriptRuntimeGAgent` 允许新增 `ScriptDefinitionQueryQueuedEvent` / `ScriptDefinitionQueryClearedEvent` 持久化 pending fact
   - `WorkflowGAgent` 仍不得为 callback runtime coordination 新增 `PersistDomainEventAsync` 持久化技术事件
3. 说明：
   - 业务事件可以保留
   - script definition query 的 `request_id + run_event + callback_id` 属于需要跨 reactivation 恢复的事实
   - activation-local callback lease 不应进入 actor 状态

### G4 callback key construction guard

1. 目标：阻止模块继续裸字符串拼 callback key / callbackId。
2. 必查规则：
   - workflow/script 模块内禁止出现以下模式：
     - `string.Concat("delay-step:"`
     - `string.Concat("wait-signal-timeout:"`
     - `string.Concat("llm-watchdog:"`
     - 其他显式 callback 前缀 + `:` 拼接
3. 要求：
   - 统一通过共享 key builder 生成 callbackId/state key
   - builder 必须对 segment 做编码或等价结构化处理

### G5 guard 接线要求

1. `tools/ci/architecture_guards.sh` 必须串联 `tools/ci/runtime_callback_guards.sh`
2. 若 guard 失败，CI 必须直接 fail
3. guard 输出必须适合本地直接复现

## 4. 必须补的测试矩阵

### T1 Foundation scheduler correctness

1. `InMemoryActorRuntimeCallbackSchedulerTests`
2. `OrleansActorRuntimeCallbackSchedulerDualStrategyTests`
3. 必测用例：
   - `CancelAsync` 只能取消同 `lease.generation`
   - Orleans `Auto` 模式长延时返回 `Dedicated` lease
   - dedicated callback cancel 必须按 `lease.Backend` 命中 dedicated scheduler
   - fired envelope 必须包含：
     - `callback_id`
     - `generation`
     - `fire_index`
     - `fired_at_unix_time_ms`
   - timer 场景的 `fire_index` 单调递增
   - old lease cancel 不得误删 new generation callback

### T2 Workflow volatile runtime semantics

1. 目标模块：
   - `DelayModule`
   - `WaitSignalModule`
   - `WorkflowLoopModule`
   - `LLMCallModule`
2. 必测用例：
   - fired callback 缺 `callback_id` 时不推进业务
   - fired callback `generation` 不匹配时不推进业务
   - fired callback `callback_id` 不匹配时不推进业务
   - 当前 activation 内 pending 记录被清空后，晚到 callback `no-op`
   - `WaitSignalModule` 中 signal/timer 共享同一规范化 stepId
   - `WorkflowLoopModule` retry/timeout 都走 callback 主链，不再依赖 `Task.Delay`
   - `LLMCallModule` watchdog timeout 只有命中当前 pending session + lease 才能发布失败完成事件

### T3 Script runtime recovery semantics

1. 目标模块：`ScriptRuntimeGAgent`
2. 必测用例：
   - definition query timeout fired 缺 metadata 时不清理/不失败
   - timeout fired `callback_id + generation` 不匹配时忽略
   - pending query 在 reactivation 后仍可接收 response 并继续提交
   - pending query 在 reactivation 后仍可接收 timeout 并继续失败收敛
   - 旧 activation 的 timeout 在新 lease 下必须 `no-op`
   - query 完成后如果收到旧 timeout，不得再次推进失败逻辑
3. 说明：
   - `pending_definition_queries` 是 ScriptRuntimeState 中允许持久化的恢复事实
   - callback lease 仍只存在于 activation runtime 索引中

### T4 Activation loss / stale callback 行为测试

1. 目标：把“activation 丢失后 no-op”写成显式验证
2. 推荐实现方式：
   - 单元测试中直接清空模块内 pending/runtime 索引，再注入晚到 callback
   - Orleans 集成测试中模拟 deactivation/re-activation 后投递旧 callback
3. 验收标准：
   - 不抛异常
   - 不推进业务
   - 最好记录 debug/warn 日志

### T5 callback key builder tests

1. 目标：确保 segment 编码后不会因 `:`、`|`、`/`、空格等字符碰撞
2. 必测用例：
   - `run_id` 含 `:`
   - `signal_name` 含 `/`
   - `step_id` 含空格或 `%`
   - 不同 segment 组合不会生成相同 key

## 5. 应删除或改写的旧测试口径

1. 任何隐含“workflow callback runtime coordination 会随 business actor state replay 恢复”的测试
2. 任何只校验 `generation`、不校验 `callback_id` 的 fired callback 测试
3. 任何默认接受“缺 metadata 也继续推进业务”的测试替身
4. `TestEventHandlerContext` 一类测试替身若只写 `CallbackGeneration`，必须补齐完整 metadata 或改为复用统一工厂

## 6. 推荐新增的测试文件

1. `test/Aevatar.Foundation.Runtime.Hosting.Tests/RuntimeCallbackEnvelopeMetadataTests.cs`
2. `test/Aevatar.Workflow.Core.Tests/Modules/VolatileRuntimeCallbackSemanticsTests.cs`
3. `test/Aevatar.Scripting.Core.Tests/Runtime/ScriptRuntimeVolatileCallbackSemanticsTests.cs`
4. `test/Aevatar.Foundation.Runtime.Hosting.Tests/RuntimeCallbackKeyBuilderTests.cs`

## 7. 完成标准（Definition of Done）

以下条件必须同时满足，才可以把这轮重构标为“完成”：

1. `RuntimeActorGrain` 不再存在 `Task.Delay` retry 第二路径
2. workflow/script callback fired 全部改成强 lease 对账
3. workflow runtime coordination 明确不进入 event-sourced business state；script 仅允许 `pending_definition_queries` 持久化
4. `tools/ci/runtime_callback_guards.sh` 已接入 `architecture_guards.sh`
5. Foundation/Workflow/Scripting 目标测试全部补齐并通过
6. 蓝图与审计文档口径一致，明确 `volatile runtime`

## 8. 建议执行顺序

1. 先补 `G1/G2/G5`
   - 先把回退路径和弱校验堵住
2. 再补 `T1/T2/T3`
   - 把语义写成测试
3. 最后补 `G3/G4/T4/T5`
   - 做语义收尾和 key/activation loss 完整治理

## 9. 本地验证命令

1. `bash tools/ci/runtime_callback_guards.sh`
2. `bash tools/ci/architecture_guards.sh`
3. `bash tools/ci/test_stability_guards.sh`
4. `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --filter Callback`
5. `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --filter Callback`
6. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter ScriptRuntime`

## 10. 结论

这份清单的作用不是再讨论“该不该做 callback recovery”，而是把“workflow 保持 volatile、script definition query 持久恢复”这个分层决策变成可以被代码审查、CI 和测试稳定执行的工程约束。

如果没有这份 guard + test 闭环，那么 callback runtime 的事实边界仍然只是口头架构，不是仓库规则。
