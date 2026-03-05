# PR Review 架构复审打分（Foundation Runtime Callback Scheduler）- 2026-03-06

## 1. 复审范围与输入

1. 复审对象：
   - `src/Aevatar.Foundation.Abstractions/Runtime/Callbacks/IActorRuntimeCallbackScheduler.cs`
   - `src/Aevatar.Foundation.Abstractions/Runtime/Callbacks/RuntimeCallbackTimeoutRequest.cs`
   - `src/Aevatar.Foundation.Abstractions/Runtime/Callbacks/RuntimeCallbackTimerRequest.cs`
   - `src/Aevatar.Foundation.Abstractions/Runtime/Callbacks/RuntimeCallbackLease.cs`
   - `src/Aevatar.Foundation.Abstractions/Runtime/Callbacks/RuntimeCallbackBackend.cs`
   - `src/Aevatar.Foundation.Abstractions/EventModules/IEventHandlerContext.cs`
   - `src/Aevatar.Foundation.Core/Pipeline/EventHandlerContext.cs`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Callbacks/OrleansActorRuntimeCallbackScheduler.cs`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming/AevatarOrleansRuntimeOptions.cs`
   - `src/Aevatar.Foundation.Runtime/Callbacks/InMemoryActorRuntimeCallbackScheduler.cs`
   - `test/Aevatar.Foundation.Runtime.Hosting.Tests/OrleansActorRuntimeCallbackSchedulerDualStrategyTests.cs`
   - `test/Aevatar.Foundation.Runtime.Hosting.Tests/InMemoryActorRuntimeCallbackSchedulerTests.cs`
2. 复审输入：
   - 上一轮 PR review 有效问题：F1 / F2。
   - 补充命名审计问题：F3。
   - 本轮要求：不考虑兼容性，按 callback scheduling 主语义彻底收敛命名与取消模型。
3. 评分口径：`docs/audit-scorecard/README.md`（100 分制、6 维度）。
4. 复审方法：
   - 仅采纳可定位到 `文件路径:行号` 的实现证据。
   - 同时核对代码、测试、架构守卫与稳定性守卫结果。

## 2. 复审结论（摘要）

1. F1 已关闭：Orleans callback 调度与取消已经共享同一个事实源，`CancelAsync` 不再根据当前 turn binding 猜测路由，而是严格按照 `RuntimeCallbackLease.Backend` 决定 inline 或 dedicated 后端。
2. F2 已关闭：InMemory scheduler 的取消从“先比 generation、再无条件删除 key”修复为“基于当前字典值实例的条件删除”，旧 lease 取消不会再误删新 generation callback。
3. F3 已关闭：抽象层与实现层命名已从 `Runtime.Async` 收敛到 `Runtime.Callbacks`，公开类型也统一到 `RuntimeCallback*` 术语，不再用 `Async` 作为错误的边界语义。
4. 相关回归测试已补齐，验证到“Auto 模式长延时落 dedicated 后仍能正确取消”与“InMemory stale lease 不误删新 generation”两个原始反例场景。
5. 结论：**本轮重构后建议合并**。当前实现满足 callback scheduling 的最小正确性契约，命名、抽象和验证口径也已经对齐。

## 3. 总体评分（100 分制）

**总分：95 / 100（A+）**

| 维度 | 权重 | 得分 | 复审结论 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 19 | callback scheduling 契约稳定停留在 Abstractions，Orleans / InMemory 实现依赖同一抽象，对外不再暴露后端猜测逻辑。 |
| CQRS 与统一投影链路 | 20 | 18 | timeout/timer 仍以内部事件形式进入 actor 主链，取消正确性缺口已关闭，不再向事件链注入陈旧 timeout。 |
| Projection 编排与状态约束 | 20 | 19 | callback lease、generation、backend 的事实源已统一；InMemory compare/remove 竞态已补齐。 |
| 读写分离与会话语义 | 15 | 15 | 取消语义与 generation CAS 保持一致，workflow timeout/watchdog 会话不会再因为错误取消路由而漂移。 |
| 命名语义与冗余清理 | 10 | 10 | `Runtime.Async` 已删除，抽象、实现、测试与文档全部统一到 `Runtime.Callbacks` / `RuntimeCallback*`。 |
| 可验证性（门禁/构建/测试） | 15 | 14 | 定向回归测试、构建、架构守卫、稳定性守卫均通过；剩余风险仅在未重新跑完整个超长集成测试矩阵。 |

## 4. 问题关闭情况与代码证据

### F1 已关闭：Orleans Auto 模式 dedicated callback 取消已绑定到 lease 后端

1. 代码证据：
   - 抽象层取消契约已改为 `CancelAsync(RuntimeCallbackLease lease)`：`src/Aevatar.Foundation.Abstractions/Runtime/Callbacks/IActorRuntimeCallbackScheduler.cs:13`。
   - `RuntimeCallbackLease` 已显式携带 `Backend`：`src/Aevatar.Foundation.Abstractions/Runtime/Callbacks/RuntimeCallbackLease.cs:3`。
   - Orleans 调度器在 dedicated 路径返回 `RuntimeCallbackBackend.Dedicated` lease：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Callbacks/OrleansActorRuntimeCallbackScheduler.cs:73`、`:107`。
   - Orleans 取消路由改为按 `lease.Backend` 分派，`Inline` 与 `Dedicated` 分支显式分开：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Callbacks/OrleansActorRuntimeCallbackScheduler.cs:114`。
   - Inline 取消在缺少当前 grain turn 绑定时直接抛错，不再吞掉 dedicated callback 的取消：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Callbacks/OrleansActorRuntimeCallbackScheduler.cs:160`。
2. 回归测试：
   - `CancelAsync_ShouldUseDedicatedBackend_FromReturnedLease_WhenAutoModeSchedulesDedicated` 已覆盖原始反例：`test/Aevatar.Foundation.Runtime.Hosting.Tests/OrleansActorRuntimeCallbackSchedulerDualStrategyTests.cs:272`。
   - 现有 inline / force dedicated cancel 测试也同步验证了 lease 后端路由：`test/Aevatar.Foundation.Runtime.Hosting.Tests/OrleansActorRuntimeCallbackSchedulerDualStrategyTests.cs:232`、`:250`。
3. 复审判断：
   - “调度到哪里”与“从哪里取消”已经收敛为同一条事实链：`Schedule*Async -> RuntimeCallbackLease.Backend -> CancelAsync`。
   - 原 review 中“Auto + 长延时 + 当前 turn 有 binding 时取消打空”这一缺口已关闭。

### F2 已关闭：InMemory cancel 的 generation compare/remove 已原子化到当前值实例

1. 代码证据：
   - InMemory 调度返回 `RuntimeCallbackBackend.InMemory` lease：`src/Aevatar.Foundation.Runtime/Callbacks/InMemoryActorRuntimeCallbackScheduler.cs:34`、`:55`。
   - 取消先校验 `lease.Backend == InMemory`，再读取当前 callback 实例：`src/Aevatar.Foundation.Runtime/Callbacks/InMemoryActorRuntimeCallbackScheduler.cs:62`。
   - generation 匹配后，删除动作改为 `Remove(new KeyValuePair<string, ScheduledCallback>(key, callback))` 条件删除，只有当前值仍是同一实例才会成功：`src/Aevatar.Foundation.Runtime/Callbacks/InMemoryActorRuntimeCallbackScheduler.cs:76`。
2. 回归测试：
   - `CancelAsync_ShouldNotRemoveNewGeneration_WhenCalledWithStaleLease` 直接覆盖“旧 lease cancel 误删新 generation”反例：`test/Aevatar.Foundation.Runtime.Hosting.Tests/InMemoryActorRuntimeCallbackSchedulerTests.cs:15`。
   - `ScheduleTimeoutAsync_ShouldReturnInMemoryBackendLease` 验证 lease 后端事实已固定：`test/Aevatar.Foundation.Runtime.Hosting.Tests/InMemoryActorRuntimeCallbackSchedulerTests.cs:43`。
3. 复审判断：
   - 旧 generation cancel 无法再跨过并发 reschedule 窗口删除新 callback，lease/CAS 语义恢复成立。

### F3 已关闭：命名已从 Runtime.Async 收敛到 Runtime.Callbacks

1. 代码证据：
   - 抽象层 namespace 已统一为 `Aevatar.Foundation.Abstractions.Runtime.Callbacks`：`src/Aevatar.Foundation.Abstractions/Runtime/Callbacks/IActorRuntimeCallbackScheduler.cs:1`。
   - 上层上下文 API 统一暴露 `RuntimeCallbackLease` 与 `ScheduleSelfTimeoutAsync` / `ScheduleSelfTimerAsync` / `CancelScheduledCallbackAsync`：`src/Aevatar.Foundation.Abstractions/EventModules/IEventHandlerContext.cs:46`、`src/Aevatar.Foundation.Core/Pipeline/EventHandlerContext.cs:62`。
   - Orleans 运行时选项也已收敛到 `RuntimeCallback*` 命名：`src/Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming/AevatarOrleansRuntimeOptions.cs:10`、`:35`、`:41`、`:47`、`:53`。
2. 重构判断：
   - `Runtime` 层次保留是正确的，因为这组能力表达的是 runtime 提供给 actor 的基础设施契约。
   - 被移除的是错误的 `Async` 边界命名；现在契约主语义已经明确为 callback scheduling。

## 5. 验证记录

1. 构建：`dotnet build aevatar.slnx --nologo -m:1`
   - 结果：通过，`0 warnings, 0 errors`。
2. 定向运行时测试：`dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --tl:off -m:1 --filter "FullyQualifiedName~OrleansActorRuntimeCallbackSchedulerDualStrategyTests|FullyQualifiedName~InMemoryActorRuntimeCallbackSchedulerTests"`
   - 结果：通过，`13 passed`。
3. Foundation Core 测试：`dotnet test test/Aevatar.Foundation.Core.Tests/Aevatar.Foundation.Core.Tests.csproj --nologo --tl:off -m:1`
   - 结果：通过，`139 passed`。
4. Workflow Core 测试：`dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --tl:off -m:1`
   - 结果：通过，`91 passed`。
5. Workflow Maker 扩展测试：`dotnet test test/Aevatar.Workflow.Extensions.Maker.Tests/Aevatar.Workflow.Extensions.Maker.Tests.csproj --nologo --tl:off -m:1`
   - 结果：通过，`14 passed`。
6. 稳定性守卫：`bash tools/ci/test_stability_guards.sh`
   - 结果：通过。
7. 架构守卫：`bash tools/ci/architecture_guards.sh`
   - 结果：通过。

## 6. 剩余风险与说明

1. 本轮没有重新执行完整解决方案的所有集成测试用例；此前直接运行 `Aevatar.Integration.Tests` 时出现长时间执行，因此本次以受影响项目测试 + 全量构建 + 守卫通过作为合并依据。
2. dedicated scheduler grain 内部接口仍保持 `callbackId + expectedGeneration` 形式，这是 dedicated backend 内部协议，不再外溢到抽象层；当前不构成命名或取消一致性问题。
3. 若后续继续扩展 callback 生命周期查询、tombstone 或状态读模型，建议仍以 `RuntimeCallbackLease` 和 `RuntimeCallback*` 术语为唯一主干，避免重新引入泛化 `Async` 边界。

## 7. 复审结论

这次重构已经把三个问题同时收敛：

1. Orleans `Auto` 模式下 dedicated callback 的取消不再走错路由。
2. InMemory scheduler 的 stale cancel 不再误删新 generation callback。
3. 抽象层与实现层命名已回到 callback scheduling 的真实语义。

基于当前代码证据、回归测试和守卫结果，结论是：**建议合并**。
