# PR Review 架构审计打分（Foundation Runtime Callback Scheduler Refactor）- 2026-03-06

## 1. 审计范围与方法

1. 审计对象：
   - `src/Aevatar.Foundation.Abstractions/Runtime/Callbacks/IActorRuntimeCallbackScheduler.cs`
   - `src/Aevatar.Foundation.Abstractions/EventModules/IEventHandlerContext.cs`
   - `src/Aevatar.Foundation.Core/GAgentBase.cs`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/DependencyInjection/ServiceCollectionExtensions.cs`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs`
   - `src/workflow/Aevatar.Workflow.Core/Modules/HumanInputModule.cs`
   - `src/workflow/Aevatar.Workflow.Core/Modules/HumanApprovalModule.cs`
   - `docs/WORKFLOW_PRIMITIVES.md`
2. 本轮目标：
   - 在前一版复审基础上，验证剩余问题是否已通过“破坏性重构”关闭。
   - 判断当前分层、回调语义、workflow replay correctness 与交互契约是否已闭环。
3. 客观验证输入：
   - `dotnet build aevatar.slnx --nologo`
   - `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo`
   - `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter "WorkflowAdditionalModulesCoverageTests|WorkflowLoopModuleCoverageTests"`
   - `bash tools/ci/test_stability_guards.sh`
   - `bash tools/ci/architecture_guards.sh`

## 2. 复审结论（摘要）

1. 之前 review 中的 3 条问题已全部关闭。
2. 公共 callback contract 仍保持 durable-only，Orleans `TurnBound` 风险面未重新引回公共层。
3. `WorkflowLoopModule` 已改成“follow-up 成功后再清理 lease / retry state”，并把 step dispatch 收口成失败可回滚、成功后提交的路径。
4. `wait_signal` / `human_input` / `human_approval` 的恢复契约已统一收紧为 `run_id` 必填；旧的 no-`run_id` best-effort fallback 被明确删除，不再作为公共语义保留。
5. 当前结论：**建议合并**。剩余风险主要在于本轮未重新跑 `dotnet test aevatar.slnx` 全量测试，而不是当前审计范围内仍存在未关闭缺口。

## 3. 总体评分（100 分制）

**总分：95 / 100（A）**

相较上一版 `82 / 100（B+）`，本轮提升来自：
1. workflow fired callback replay correctness 已补齐。
2. 交互恢复链路的 run-scoped contract 已统一，不再在模块内部保留模糊回退。
3. 回归测试、稳定性 guard 与架构 guard 已覆盖这轮关键收口点。

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 19 | 公共层继续只暴露 durable 语义，未重新泄漏 Orleans turn-bound 细节。 |
| CQRS 与统一投影链路 | 20 | 19 | callback 仍通过事件化主链推进，workflow follow-up replay correctness 已补齐。 |
| Projection 编排与状态约束 | 20 | 19 | timeout / retry-backoff / 交互恢复都已回到“Actor 内对账、成功后提交”的模型。 |
| 读写分离与会话语义 | 15 | 15 | `run_id` 契约明确、单一，去掉了 no-`run_id` 猜测语义。 |
| 命名语义与冗余清理 | 10 | 10 | 删除优于兼容的方向贯彻到底，公共无效层与模糊回退均已清掉。 |
| 可验证性（门禁/构建/测试） | 15 | 13 | build / tests / guards 已过；本轮未重跑全量 `dotnet test aevatar.slnx`，保留 2 分谨慎扣减。 |

## 4. 已关闭问题

### C1 原 F1 已保持关闭：公共 callback 语义继续是 durable-only

1. 证据：
   - 公共接口仍是单一 durable contract：`src/Aevatar.Foundation.Abstractions/Runtime/Callbacks/IActorRuntimeCallbackScheduler.cs`
   - 事件上下文只保留 durable 调度 API：`src/Aevatar.Foundation.Abstractions/EventModules/IEventHandlerContext.cs`
   - Orleans DI 只注册 durable scheduler：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/DependencyInjection/ServiceCollectionExtensions.cs`
2. 影响：
   - 业务层无法再依赖 grain-turn / inline 语义。
   - 之前由公共 `TurnBound` 暴露导致的语义混淆已继续被隔离在基础设施内部。

### C2 原 F2 已关闭：WorkflowLoop fired callback 现在可重放

1. 证据：
   - timeout fired 先 `PublishAsync(StepCompletedEvent)`，成功后才 `_timeouts.Remove(...)`：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:97-147`
   - retry 计数只在 backoff/redispatch成功后提交，不再在 transient failure 前提前消耗：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:330-387`
   - retry-backoff fired 先 `DispatchStep(...)`，成功后才 `_retryBackoffs.Remove(...)`：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:390-453`
   - `DispatchStep(...)` 现在先准备 timeout lease 与 request，失败时回滚 timeout，成功后才提交 `currentStep/currentInput/_timeouts`：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:539-691`
   - run 首次启动也增加了 dispatch failure rollback，避免“run 已激活但首步未真正启动”的脏态：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:48-95`
2. 回归测试：
   - timeout follow-up 首次发布失败后，重放同一 fired callback 仍可失败收敛：`test/Aevatar.Workflow.Core.Tests/Modules/RuntimeCallbackEventizationTests.cs:153-215`
   - retry-backoff redispatch 首次失败后，重放同一 fired callback 仍可重新派发 step：`test/Aevatar.Workflow.Core.Tests/Modules/RuntimeCallbackEventizationTests.cs:277-357`
3. 影响：
   - 当前 workflow callback path 已重新满足“回调只发信号，业务推进在 Actor 事件处理中完成，且可重放”的仓库约束。

### C3 原 F3 已关闭：交互恢复契约已改为显式 run-scoped，兼容 fallback 被删除

1. 证据：
   - `WaitSignalModule` 只接受带 `run_id` 的 `SignalReceivedEvent`，不再做 no-`run_id` 单匹配猜测：`src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs:206-239`
   - `HumanInputModule` 与 `HumanApprovalModule` 也统一要求 `WorkflowResumedEvent.run_id`：`src/workflow/Aevatar.Workflow.Core/Modules/HumanInputModule.cs:124-136`、`src/workflow/Aevatar.Workflow.Core/Modules/HumanApprovalModule.cs:125-137`
   - 文档已同步声明 `WorkflowResumedEvent` / `SignalReceivedEvent` 必须显式携带 `run_id`：`docs/WORKFLOW_PRIMITIVES.md:204-209`、`docs/WORKFLOW_PRIMITIVES.md:503-511`
2. 回归测试：
   - `wait_signal` 即便只有单 waiter，缺失 `run_id` 也不会恢复：`test/Aevatar.Integration.Tests/WorkflowAdditionalModulesCoverageTests.cs:210-239`
   - `human_approval` 缺失 `run_id` 不会恢复：`test/Aevatar.Integration.Tests/WorkflowAdditionalModulesCoverageTests.cs:755-784`
   - `human_input` 缺失 `run_id` 不会恢复：`test/Aevatar.Integration.Tests/WorkflowAdditionalModulesCoverageTests.cs:946-975`
3. 影响：
   - 这条问题不是通过“恢复旧兼容行为”关闭，而是通过 breaking-contract 收口关闭。
   - 当前 API 字段语义重新单一：`run_id` 只表达 run 关联，不再混入“缺失时尝试猜一个 waiter”的隐式语义。

## 5. 可验证性复核

### 5.1 已通过的客观验证

1. `dotnet build aevatar.slnx --nologo`
   - 结果：通过，`0 warnings / 0 errors`
2. `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo`
   - 结果：`93 passed`
3. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter "WorkflowAdditionalModulesCoverageTests|WorkflowLoopModuleCoverageTests"`
   - 结果：`65 passed`
4. `bash tools/ci/test_stability_guards.sh`
   - 结果：通过
5. `bash tools/ci/architecture_guards.sh`
   - 结果：通过

### 5.2 本轮新增覆盖点

1. callback replay after transient follow-up failure
   - 已在 `RuntimeCallbackEventizationTests` 中对 timeout/retry-backoff 两条路径补齐。
2. interactive resume/signal strict run-scoped contract
   - 已在 `WorkflowAdditionalModulesCoverageTests` 中对 `wait_signal` / `human_input` / `human_approval` 的缺失 `run_id` 场景补齐。

### 5.3 仍保留的验证边界

1. 本轮没有重新执行 `dotnet test aevatar.slnx --nologo`。
2. 由于这是破坏性契约收口，外部调用方若仍发送缺失 `run_id` 的恢复/信号事件，将被明确拒绝；这不是测试缺口，而是设计结果。

## 6. 合并门禁（当前）

1. 旧门禁已满足：
   - workflow fired callback replay correctness：已满足
   - interactive correlation contract 明确化：已满足
   - 回归测试与 guard：已满足
2. 当前唯一保留项：
   - 若要作为最终发布前的完整质量关口，建议再补跑一次 `dotnet test aevatar.slnx --nologo`。

## 7. 审计结论

这轮重构已经从架构抽象和 workflow 运行时两侧把问题真正收口了：

1. 公共 callback contract 继续保持 durable-only，没有把 Orleans turn-bound 语义重新暴露回框架层。
2. `WorkflowLoopModule` 把 timeout / retry-backoff / step dispatch 改成了可重放、可回滚、成功后提交的 Actor 内事务式路径。
3. `wait_signal` / `human_input` / `human_approval` 去掉了模糊兼容回退，显式要求 `run_id`，文档与测试同步更新。

因此，本轮复审结论是：**95 / 100（A）**，**建议合并**。

保留说明：

- 本轮已完成 `build + 定向 tests + stability guard + architecture guard`。
- 本轮未重新执行全量 `dotnet test aevatar.slnx --nologo`。
