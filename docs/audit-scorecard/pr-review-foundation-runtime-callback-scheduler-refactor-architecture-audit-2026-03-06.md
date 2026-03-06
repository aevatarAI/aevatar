# PR Review 架构审计打分（Foundation Runtime Callback Scheduler Refactor）- 2026-03-06

## 1. 审计范围与方法

1. 审计对象：
   - `src/Aevatar.Foundation.Abstractions/Runtime/Callbacks/IActorRuntimeCallbackScheduler.cs`
   - `src/Aevatar.Foundation.Abstractions/EventModules/IEventHandlerContext.cs`
   - `src/Aevatar.Foundation.Core/GAgentBase.cs`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Callbacks/OrleansActorRuntimeDurableCallbackScheduler.cs`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/DependencyInjection/ServiceCollectionExtensions.cs`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs`
   - `docs/architecture/actor-runtime-stream-callback-request-reply-capability-refactor-blueprint-2026-03-05.md`
2. 评分口径：`docs/audit-scorecard/README.md`（100 分制、6 维度）。
3. 本轮定位：
   - 基于当前代码重构后的复审。
   - 目标是判断之前 review 的 3 条问题哪些已关闭，哪些仍保留。
4. 客观验证输入：
   - `dotnet build aevatar.slnx --nologo`
   - `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --no-build`
   - `dotnet test test/Aevatar.Foundation.Core.Tests/Aevatar.Foundation.Core.Tests.csproj --nologo --no-build`
   - `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --no-build`
   - `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --no-build`
   - `dotnet test test/Aevatar.Workflow.Extensions.Maker.Tests/Aevatar.Workflow.Extensions.Maker.Tests.csproj --nologo --no-build`
   - `bash tools/ci/test_stability_guards.sh`
   - 针对残余风险的测试扫描：
     - `rg -n "PublishAsync.*throw|DispatchStep.*throw|run_id|RunId = \"\"|SignalReceivedEvent\\s*\\{|not send run_id|single waiter|fallback" test/Aevatar.Workflow.Core.Tests`
     - `rg -n "ForceInline|RuntimeCallbackSchedulingMode|turn-bound|inline binding|active grain turn" test/Aevatar.Foundation.Runtime.Hosting.Tests`

## 2. 复审结论（摘要）

1. 之前 review 中最严重的架构问题已经关闭：
   - 公共 callback contract 已收口为 durable-only。
   - 公共 `TurnBound` API、generic kind/tag、Orleans turn-bound wrapper 与 inline binding 公共暴露已删除。
   - runtime delayed retry 现在固定走 durable scheduler，不再依赖已解绑的 grain turn。
2. 但 workflow 侧仍保留 2 条实质问题：
   - `WorkflowLoopModule` timeout / retry-backoff 在 follow-up 成功前提前清除 lease，fired callback 仍不可重放。
   - `WaitSignalModule` 仍删除了无 `run_id` 的单匹配 fallback，旧调用方兼容性仍未恢复。
3. 结论：
   - 本次重构相较旧版明显提升，主架构方向已经对齐。
   - 但当前仍 **不建议合并**，因为剩余两项问题会继续影响 workflow 正确性与兼容语义。

## 3. 总体评分（100 分制）

**总分：82 / 100（B+）**

与上一版 `68 / 100（C）` 相比，主架构问题已关闭，评分提升主要来自：
1. 公共抽象语义显式收口为 durable-only。
2. Orleans runtime retry 不再依赖 turn-bound 动态选择。
3. 公共无效层删除更彻底。
4. build/test/guard 已完成并通过。

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 19 | 公共层不再泄漏 Orleans grain-turn 语义，业务层只依赖 durable contract。 |
| CQRS 与统一投影链路 | 20 | 17 | runtime callback 主链已统一，但 workflow fired callback 仍存在不可重放分支。 |
| Projection 编排与状态约束 | 20 | 15 | callback 继续事件化推进正确，但 timeout/backoff lease 提前清除仍破坏 Actor 内对账。 |
| 读写分离与会话语义 | 15 | 12 | 会话主链基本清晰，但 `wait_signal` 无 `run_id` fallback 仍是兼容回退。 |
| 命名语义与冗余清理 | 10 | 10 | 删除了无实际 caller 的公共 `TurnBound` 壳层，命名与职责更一致。 |
| 可验证性（门禁/构建/测试） | 15 | 9 | build/test/guard 已通过，但残余两条问题仍缺回归测试覆盖。 |

## 4. 已关闭问题

### C1 原 F1 已关闭：公共 callback 语义已收口为 durable-only

1. 证据：
   - 公共接口已明确声明 durable-only 语义：`src/Aevatar.Foundation.Abstractions/Runtime/Callbacks/IActorRuntimeCallbackScheduler.cs:3-19`
   - 事件上下文只保留 durable 调度方法：`src/Aevatar.Foundation.Abstractions/EventModules/IEventHandlerContext.cs:46-66`
   - `GAgentBase` 只解析单一 `IActorRuntimeCallbackScheduler`：`src/Aevatar.Foundation.Core/GAgentBase.cs:230-285`
   - Orleans DI 只注册 durable scheduler：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/DependencyInjection/ServiceCollectionExtensions.cs:58-66`
   - Orleans 选项中已不再出现 `RuntimeCallbackSchedulingMode / ForceInline / ForceDedicated` 语义开关，只剩 durable 后端策略：`src/Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming/AevatarOrleansRuntimeOptions.cs:10-38`
2. 影响：
   - 业务层无法再误把“是否依赖当前 grain turn”当作公共能力选择。
   - 之前“同一签名承载两种语义”的根因已被移除。

### C2 原 F1 的 runtime 路径已关闭：delayed retry 固定走 durable scheduler

1. 证据：
   - `RuntimeActorGrain.TryScheduleRetryAsync` 直接解析 `IActorRuntimeCallbackScheduler` 并调用 durable timeout：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs:377-388`
   - Orleans durable scheduler 统一经 dedicated callback grain 调度：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Callbacks/OrleansActorRuntimeDurableCallbackScheduler.cs:34-135`
2. 影响：
   - 之前“离开 inline binding 后调度重试”的错误路径已不存在。
   - runtime retry correctness 已与公共抽象对齐。

### C3 公共冗余层已删除

1. 证据：
   - 蓝图文档已更新为 durable-only 方案：`docs/architecture/actor-runtime-stream-callback-request-reply-capability-refactor-blueprint-2026-03-05.md`
   - Orleans callback scheduler 测试已收口为 durable-only：`test/Aevatar.Foundation.Runtime.Hosting.Tests/OrleansActorRuntimeCallbackSchedulerTests.cs:13-128`
2. 影响：
   - 当前架构不再保留“没有真实业务 caller 的公共 TurnBound 壳层”。
   - 删除方向符合仓库规则：`删除优于兼容`、`不保留无效层`。

## 5. 仍然开放的问题

### F2（P2）WorkflowLoop timeout / retry-backoff fired callback 仍不可重放

1. 证据：
   - timeout 分支仍先 `_timeouts.Remove(stepRunKey)`，后 `PublishAsync(StepCompletedEvent)`：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:130-138`
   - retry-backoff 分支仍先 `_retryBackoffs.Remove(stepRunKey)`，后 `DispatchStep(...)`：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:421-442`
   - `DispatchStep(...)` 后续还会继续启动 timeout 并 publish 新 step request：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:606-626`
2. 影响：
   - 一旦 `ctx.PublishAsync` 或 `DispatchStep` 内部 publish 瞬时失败，同一 fired callback replay 时会因为活跃 lease 已被清空而直接 no-op。
   - 这仍会把 workflow 卡在旧 step，破坏当前“callback fired -> actor 内对账 -> 继续推进”的事件化模型。
3. 当前状态判断：
   - 架构主线已修，但 workflow correctness 仍未闭环。
   - 这是当前最主要的剩余阻断项。

### F3（P2）WaitSignal 仍未恢复无 `run_id` 的单匹配 fallback

1. 证据：
   - `TryResolvePending(...)` 对空 `signal.RunId` 仍直接 `return false`：`src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs:201-203`
   - 只有有 `run_id` 时才继续在同 run 下做 `step_id` 或单匹配分支：`src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs:205-225`
2. 影响：
   - 旧调用方如果只发送 `signal_name`，依赖“只有一个 waiter 就自动命中”的兼容语义，当前仍无法恢复 `wait_signal`。
   - 这仍是协议兼容回退，不是纯内部重构细节。

## 6. 可验证性复核

### 6.1 已通过的客观验证
1. `dotnet build aevatar.slnx --nologo`
   - 结果：通过，`0 warnings / 0 errors`
2. `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --no-build`
   - 结果：`142 passed, 16 skipped`
3. `dotnet test test/Aevatar.Foundation.Core.Tests/Aevatar.Foundation.Core.Tests.csproj --nologo --no-build`
   - 结果：`139 passed`
4. `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --no-build`
   - 结果：`91 passed`
5. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --no-build`
   - 结果：`180 passed`
6. `dotnet test test/Aevatar.Workflow.Extensions.Maker.Tests/Aevatar.Workflow.Extensions.Maker.Tests.csproj --nologo --no-build`
   - 结果：`14 passed`
7. `bash tools/ci/test_stability_guards.sh`
   - 结果：通过

### 6.2 仍然缺失的回归覆盖
1. `rg -n "PublishAsync.*throw|DispatchStep.*throw|run_id|RunId = \"\"|SignalReceivedEvent\\s*\\{|not send run_id|single waiter|fallback" test/Aevatar.Workflow.Core.Tests`
   - 结果：无匹配
   - 说明：当前 workflow tests 没有显式覆盖“timeout/retry fired 后 follow-up 首次失败再 replay”与“无 `run_id` fallback”。
2. `rg -n "ForceInline|RuntimeCallbackSchedulingMode|turn-bound|inline binding|active grain turn" test/Aevatar.Foundation.Runtime.Hosting.Tests`
   - 结果：无匹配
   - 说明：旧的 turn-bound/ForceInline 风险点已经从公共架构与测试面删除，符合本次 durable-only 收口。

## 7. 合并门禁（当前）

1. 必须修复 F2：
   - timeout fired 后首次 `PublishAsync` 失败，重放同一 fired callback 仍能完成失败收敛。
   - retry-backoff fired 后首次 `DispatchStep` 失败，重放同一 fired callback 仍能重新派发 step。
2. 必须修复 F3：
   - 恢复无 `run_id` 且单 waiter 时的兼容 fallback。
   - 多 waiter 仍保持拒绝自动匹配。
3. 必须补两类回归测试：
   - workflow fired callback replay after transient follow-up failure
   - `wait_signal` no-`run_id` single-match fallback

## 8. 审计结论

这次重构已经把最核心的架构问题修正了：公共 callback contract 不再混合 `turn-bound` 与 `durable` 语义，Orleans runtime retry 也不再依赖已解绑的 grain-turn 上下文。从架构主线看，这次收口是正确的，且明显优于上一版。

但 workflow 侧仍保留两个实质性行为问题：`WorkflowLoopModule` fired callback 不可重放，以及 `WaitSignalModule` 无 `run_id` 兼容路径缺失。它们不是风格问题，而是仍会影响正确性和兼容性的未关闭缺口。

因此，本轮复审结论是：**82 / 100（B+）**，**较上一版显著改善，但当前仍不建议合并**。待 F2/F3 关闭并补齐对应回归测试后，可再进入下一轮复评。
