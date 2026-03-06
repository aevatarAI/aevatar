# PR Review 架构审计打分（Foundation Runtime Callback Scheduler Refactor）- 2026-03-06

## 1. 审计范围与输入

1. 审计对象：
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs`
   - `src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto`
2. 输入来源：
   - 本次 PR review 的 3 条有效问题：`P1 x 1`、`P2 x 2`。
   - 源码复核证据（按 `文件:行号` 与历史命令输出交叉验证）。
3. 评分口径：`docs/audit-scorecard/README.md`（100 分制、6 维度）。
4. 本轮审计定位：
   - 这是一次基于 review 结果的文档重生成，不是代码修复报告。
   - reviewer 已指出“updated tests pass”，但当前通过结果不能覆盖已发现的行为回归与兼容回退。

## 2. 审计结论（摘要）

1. 本次重构仍把 `turn-bound` 与 `durable` 两种 callback 语义混在单一 scheduler 抽象里，导致 runtime retry 延迟路径在 Orleans `RuntimeCallbackSchedulingMode=ForceInline` 下误用了已解绑的 grain-turn 能力，支持配置会直接退化为“无法延迟重试”。
2. workflow timeout / retry-backoff fired callback 在 follow-up publish 或 redispatch 成功前就清除了活跃 lease；一旦后续 publish 因瞬时故障失败，回放同一 fired callback 会直接 no-op，workflow 可卡死在旧 step。
3. `wait_signal` 删除了无 `run_id` 输入的单匹配兼容路径，而协议层 `SignalReceivedEvent.run_id` 仍是可省略字段，旧调用方会失去恢复能力。
4. 结论：**当前不建议合并**。需先关闭 1 条 P1 和 2 条 P2，并补齐对应回归测试。

## 3. 总体评分（100 分制）

**总分：68 / 100（C）**

| 维度 | 权重 | 得分 | 扣分说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 18 | 抽象边界仍基本正确，但框架层未显式拆分 turn-bound / durable 语义，导致 Orleans grain turn 绑定语义被误用。 |
| CQRS 与统一投影链路 | 20 | 13 | runtime retry 与 workflow callback 主链存在可执行但不可重放的分支，未守住单一延迟主链。 |
| Projection 编排与状态约束 | 20 | 11 | fired callback lease 在成功前提前清除，破坏 Actor 内 volatile 运行态对账。 |
| 读写分离与会话语义 | 15 | 10 | `wait_signal` 兼容路径回退，workflow timeout/backoff 失败后可导致会话停滞。 |
| 命名语义与冗余清理 | 10 | 9 | `Runtime.Callbacks` 与 callback key 收敛方向正确，本轮问题不主要出在命名。 |
| 可验证性（门禁/构建/测试） | 15 | 7 | 现有测试未覆盖 ForceInline 延迟重试、瞬时 publish 失败回放、无 `run_id` 兼容恢复。 |

## 4. 问题分级与证据

### F1（P1）ForceInline 模式下 delayed retry 逃离了已绑定 grain turn

1. 证据：
   - `HandleEnvelopeAsync` 只在 `try` 内绑定 state 与 inline scheduler，上下文绑定范围是 `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs:179`-`:183`；`catch` 位于 `:185`-`:187`。
   - `TryScheduleRetryAsync` 在 `catch` 后通过 `ServiceProvider.GetRequiredService<IActorRuntimeCallbackScheduler>()` 调度延迟重试：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs:376`-`:395`。
2. 影响：
   - 当 `RuntimeCallbackSchedulingMode=ForceInline` 且 `RetryDelayMs > 0` 时，延迟调度发生在 `inlineSchedulerBinding` 已释放之后。
   - 结果不是“重试被推迟”，而是 `ScheduleTimeoutAsync` 在无 active inline binding 场景抛错，runtime auto-retry 直接停摆。
   - 根因不是单个 `catch` 写错，而是框架层仍把“依赖当前 grain turn 的 turn-bound callback”与“可脱离当前 turn 的 durable callback”揉进同一个 scheduler 契约里。
3. 修复准入：
   - 框架层必须显式拆分 `turn-bound callbacks` 与 `durable callbacks` 两类能力，不再依赖 `ForceInline / ForceDedicated` 运行时切换语义。
   - runtime delayed retry 必须改走 `durable callbacks`；不得继续使用“单一 scheduler + grain turn 绑定碰运气”的路径。
   - 必须新增 Orleans 集成回归：`ForceInline + RetryDelayMs > 0` 下首跳失败后仍能成功排入下一次 retry。

### F2（P2）workflow timeout / retry-backoff lease 在 follow-up 成功前被提前清除，导致 fired callback 不可重放

1. 证据：
   - timeout 分支先执行 `_timeouts.Remove(stepRunKey)`，再 `PublishAsync(new StepCompletedEvent(...))`：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:130`-`:138`。
   - retry-backoff 分支先执行 `_retryBackoffs.Remove(stepRunKey)`，再 `DispatchStep(...)`：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:421`-`:441`。
   - `DispatchStep` 内部还会继续调用 `StartTimeoutAsync` 与 `ctx.PublishAsync(req, ...)`：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:593`-`:594`。
2. 影响：
   - 如果 `ctx.PublishAsync`、`StartTimeoutAsync` 或 `DispatchStep` 内 publish 因 stream/provider 瞬时故障抛错，原 fired callback 在 replay 时会因为 `_timeouts` / `_retryBackoffs` 已被清空而直接 no-op。
   - 这与当前 `volatile runtime + fired callback 对账` 语义冲突，会把 workflow 卡在旧 step，且没有第二次推进机会。
3. 修复准入：
   - lease 只能在 follow-up publish / redispatch 成功后再清除，或在失败时恢复活跃 lease 以支持同一 fired callback 重放。
   - 必须补两条回归测试：
     - timeout fired 后首次 `PublishAsync` 抛错，重复投递同一 fired callback 仍能完成失败收敛。
     - retry-backoff fired 后首次 `DispatchStep` 抛错，重复投递同一 fired callback 仍能重新派发 step。

### F3（P2）`wait_signal` 删除了无 `run_id` 的兼容回退路径

1. 证据：
   - 当前实现对空 `signal.RunId` 直接 `return false`：`src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs:201`-`:203`。
   - 协议层 `SignalReceivedEvent` 仍仅定义普通 `string run_id = 3;`，未提升为强制语义：`src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto:56`。
   - 历史命令证据：`git show cee142de:src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs | sed -n '120,149p'` 显示旧实现保留了 “old clients may not send run_id” 的单匹配兼容逻辑。
2. 影响：
   - 旧调用方若仅发送 `signal_name`，并依赖“当前只有一个 waiter”这一兼容语义，将无法再恢复 `wait_signal` 步骤。
   - 这不是内部重构细节变化，而是显式兼容行为回退。
3. 修复准入：
   - 恢复无 `run_id` 时的单匹配 fallback；仅在同一 `signal_name` 下存在唯一 waiter 时自动命中，否则保持拒绝。
   - 必须补回归测试，覆盖“无 `run_id` 且仅一个匹配 waiter”可恢复，以及“多个 waiter”仍拒绝自动匹配。

## 5. 合并门禁（Blocking Exit Criteria）

1. 修复 F1：保证 `ForceInline + delayed retry` 在受支持配置下仍能完成排程，不能依赖已解绑上下文。
2. 修复 F2：timeout / retry-backoff fired callback 必须具备单次瞬时失败后的可重放性，不能因 lease 提前删除而卡死 workflow。
3. 修复 F3：恢复 `wait_signal` 对无 `run_id` 旧调用方的单匹配兼容路径。
4. 至少补跑并通过：
   - `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo`
   - `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo`
   - `bash tools/ci/test_stability_guards.sh`

## 6. 设计收口建议

1. 框架层 callback 能力应直接拆成 `turn-bound` 与 `durable` 两类接口，而不是继续暴露“单一 scheduler + 运行时模式切换”。
2. `LocalActor` 可以让两类接口共用同一套 in-memory 实现；差异体现在契约，不要求体现在实现类数量。
3. `Orleans` 应明确映射为：
   - `turn-bound` -> 当前 grain turn 内 inline scheduler
   - `durable` -> dedicated callback engine（timer/reminder 为内部后端策略）
4. `ForceInline / ForceDedicated / Auto` 若继续保留，只应作为 Orleans 基础设施内部迁移开关，不应再作为业务侧可依赖语义。

## 7. 验证说明

1. 本轮仅重生成 PR review 架构审计文档，未重复执行 `build/test/guards`。
2. 客观输入以 reviewer 的 3 条 findings 与本地源码复核为准。
3. “updated tests pass” 不能作为放行依据，因为本次 3 条问题都属于现有测试未覆盖的行为回归或兼容回退。

## 8. 审计结论

本 PR 当前问题是运行时正确性回归，不是命名或风格问题。尤其是 `ForceInline` delayed retry 回归暴露出框架层把 `turn-bound / durable` 两类 callback 语义混成单一接口的缺口；workflow fired callback 不可重放和 `wait_signal` 无 `run_id` fallback 删除则分别触及主收敛路径与兼容语义。

因此，当前结论维持为：**不建议合并**。待 3 项问题关闭、补回归并完成目标测试后，再进入复评。
