# PR Review 修复复评打分（Durable Callback + Actorized Run）- 2026-03-08

## 1. 复评范围

1. 对照基线：
   - `docs/audit-scorecard/pr-review-durable-callback-actorized-run-regression-scorecard-2026-03-08.md`
2. 本轮目标：关闭基线文档中的 `F1/F2/F3` 三条问题，并确认 runtime retry、actor destroy、script recovery 三条链路具备回归测试与门禁验证。

## 2. 修复结论

1. `F1` 已关闭：runtime delayed retry 现在走显式 `EnvelopeRedelivery` 语义，延迟重投不再把原始 envelope 隐式改写成 callback fired self-event。
2. `F2` 已关闭：Orleans 与 Local runtime 的 `DestroyAsync(...)` 都会按 actor id purge durable callbacks，销毁语义与 callback 生命周期已对齐。
3. `F3` 已关闭：script definition query recovery 使用 remaining timeout；超过预算的 pending query 会在恢复时立即超时收敛。
4. 当前复评结论：**98 / 100（A+）**，**建议合并**。

## 3. 复评评分（100 分制）

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | runtime callback delivery 语义已显式化，runtime 与 scheduler 的职责边界清晰。 |
| CQRS 与统一投影链路 | 20 | 19 | delayed retry 现可保留原始 envelope 语义，sender-sensitive handler 不再被 runtime 重写破坏。 |
| Projection 编排与状态约束 | 20 | 20 | actor destroy 与 durable callback state 统一进入 teardown，避免陈旧 callback 变成跨实例事实源。 |
| 读写分离与会话语义 | 15 | 15 | script pending query 使用剩余超时预算，恢复后仍保持原始超时语义。 |
| 命名语义与冗余清理 | 10 | 10 | 新增 `RuntimeCallbackDeliveryMode` 后，callback fired 与 envelope redelivery 语义不再混叠。 |
| 可验证性（门禁/构建/测试） | 15 | 14 | 定向回归、全量构建、全量测试均通过；仅保留外部环境依赖的集成测试跳过。 |

## 4. 关键修复点

### F1：delayed retry 保留原始 envelope 语义

1. 新增 `RuntimeCallbackDeliveryMode`，将 scheduler delivery 分为：
   - `FiredSelfEvent`
   - `EnvelopeRedelivery`
2. `RuntimeActorGrain` 的 delayed runtime retry 显式使用 `EnvelopeRedelivery`。
3. Orleans/InMemory callback scheduler 与 envelope factory 统一按 delivery mode 生成最终投递 envelope。
4. 回归测试覆盖：
   - `test/Aevatar.Foundation.Runtime.Hosting.Tests/OrleansActorRuntimeCallbackSchedulerTests.cs`
   - `test/Aevatar.Foundation.Runtime.Hosting.Tests/InMemoryActorRuntimeCallbackSchedulerTests.cs`
   - `test/Aevatar.Foundation.Runtime.Hosting.Tests/RuntimeCallbackEnvelopeFactoryTests.cs`

### F2：destroy 时统一 purge callbacks

1. Orleans runtime destroy 会 purge dedicated callback scheduler grain。
2. Local runtime destroy 也会通过 `IActorRuntimeCallbackScheduler.PurgeActorAsync(...)` 清理当前 actor 的 runtime callbacks。
3. 回归测试覆盖：
   - `test/Aevatar.Foundation.Runtime.Hosting.Tests/OrleansActorRuntimeForwardingTests.cs`
   - `test/Aevatar.Foundation.Runtime.Hosting.Tests/LocalActorRuntimeForwardingTests.cs`

### F3：script recovery 按 remaining timeout 收敛

1. recovery 时基于 `QueuedAtUnixTimeMs` 计算剩余超时时间。
2. 已超预算的 pending query 不再重挂新 lease，而是立即失败收敛。
3. 回归测试覆盖：
   - `test/Aevatar.Scripting.Core.Tests/Runtime/ScriptRuntimeGAgentEventDrivenQueryTests.cs`

## 5. 客观验证记录

1. `bash tools/ci/architecture_guards.sh`
   - 结果：通过。
2. `bash tools/ci/test_stability_guards.sh`
   - 结果：通过。
3. `dotnet build aevatar.slnx --nologo`
   - 结果：通过，`0 warning / 0 error`。
4. `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo`
   - 结果：通过，`149 passed / 16 skipped`。
5. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo`
   - 结果：通过，`183 passed`。
6. `dotnet test aevatar.slnx --nologo`
   - 结果：通过；关键测试域全部通过，部分外部环境依赖集成测试按既有条件跳过。

## 6. 复评结论

基线文档里的三条问题都已关闭，而且这次不是靠局部补丁规避，而是通过明确 delivery contract、统一 destroy teardown、补齐 recovery timeout 语义来收敛。

因此，本轮修复复评结论为：**98 / 100（A+）**，**建议合并**。
