# GAgent 协议优先第三阶段任务清单：Scripting Query / Observation 单主链收敛（2026-03-12）

## 1. 文档元信息

- 状态：Completed
- 版本：R2
- 日期：2026-03-12
- 关联文档：
  - `docs/architecture/2026-03-12-gagent-protocol-second-phase-scripting-evolution-task-list.md`
  - `docs/SCRIPTING_ARCHITECTURE.md`
  - `docs/FOUNDATION.md`
  - `AGENTS.md`
- 文档定位：
  - 本文定义第二阶段完成后的真实第三阶段。
  - 本阶段不再回头重做 Foundation actor 通信边界，也不再重写第二阶段已经完成的 session/manager actor ownership。
  - 本阶段只收敛 `Scripting` 剩余的 query / observation 双轨问题，让 definition 查询与 evolution completion 都回到单一主线。

## 2. 为什么第三阶段应该从这里开始

第二阶段已经解决了最核心的 actor 边界问题：

1. `ScriptEvolutionSessionGAgent` 已成为 proposal execution owner。
2. `ScriptEvolutionManagerGAgent` 已收窄为索引 actor。
3. self-message 已经 runtime-neutral，Local / Orleans 语义一致。

当前真正剩余的主债，不在执行 ownership，而在 `Scripting` 里仍然保留两套查询/完成态路径。

### 2.1 definition snapshot 仍然有模式分叉

当前代码仍然保留：

1. `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs`
   - `HandleRunScriptRequested(...)` 按 `UseEventDrivenDefinitionQuery` 分支。
2. `src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptDefinitionSnapshotPort.cs`
   - 继续暴露 `UseEventDrivenDefinitionQuery`。
3. `src/Aevatar.Scripting.Infrastructure/Ports/DefaultScriptingRuntimeQueryModes.cs`
   - 继续通过 runtime 类型推断 Local / Orleans 行为。

这意味着：

1. 运行路径仍然不是单主线。
2. Local 正确与 Orleans 正确还没有完全等价。
3. 运行时行为仍然依赖 `runtime.GetType().FullName` 这种脆弱判定。

### 2.2 evolution completion 仍然是双轨观察

当前 completion 链路实际上分成两层：

1. 主链路：
   `ScriptEvolutionSessionCompletedEvent -> ScriptEvolutionSessionCompletedEventProjector -> ProjectionSessionEventHub`
2. timeout 后 fallback：
   `ScriptEvolutionDurableCompletionResolver -> RuntimeScriptEvolutionDecisionFallbackPort -> QueryScriptEvolutionDecisionRequestedEvent`

这带来的问题是：

1. 主链路已经是 projection observation，但 timeout 后又退回 actor query。
2. 外部 interaction 对“完成态”没有唯一观察模型。
3. 这仍然偏离仓库的读写分离原则。

### 2.3 超时与模式配置仍是泛化口袋

当前还保留：

1. `src/Aevatar.Scripting.Infrastructure/Ports/IScriptingPortTimeouts.cs`
2. `src/Aevatar.Scripting.Infrastructure/Ports/IScriptingRuntimeQueryModes.cs`

这两个 abstraction 目前仍偏“大口袋式总开关/总超时”，不足以表达清晰的职责边界。

## 3. 第三阶段目标

第三阶段只完成四件事：

1. definition snapshot 查询统一为事件化单主线，不再按 runtime 类型或布尔开关分叉。
2. evolution completion 统一为 projection-first observation，不再在主链路 timeout 后回退 actor query。
3. query / observation 的配置与 timeout 拆成窄而明确的 typed options。
4. scripting 外部交互继续走 CQRS generic interaction，但其完成态来源只保留一条权威路径。

## 4. 完成定义

第三阶段完成时，必须满足：

1. `UseEventDrivenDefinitionQuery` 已从主链路删除，不再作为 runtime 行为开关。
2. `ScriptRuntimeGAgent` 的 definition 获取路径只有一条 actor-friendly、activation-safe 的事件化路径。
3. `RuntimeScriptEvolutionDecisionFallbackPort` 不再参与主链路 completion 判定；若保留，也只能作为显式 admin/debug query 能力。
4. `RuntimeScriptEvolutionInteractionService` 的完成态只依赖 projection observation 及其同源 durable 事实，不再 timeout 后回查 session actor。
5. `IScriptingPortTimeouts` / `IScriptingRuntimeQueryModes` 不再作为宽泛总口存在，或至少被职责更明确的 typed options 替代。
6. Local、Orleans、3-node、mixed-version 回归保持通过。

## 5. 非目标

第三阶段不做以下内容：

1. 不重做第二阶段已完成的 session / manager actor ownership。
2. 不把 workflow / scripting / static 再统一成新的公共 messaging abstraction。
3. 不把 `IStream.ProduceAsync` 的命名清理当成第三阶段主线。
4. 不在 Host/Application 层新增进程内 session/context 字典作为 completion 事实源。
5. 不把 script evolution completion 重新塞回 write actor 内部状态直读。

## 6. 目标架构

### 6.1 definition snapshot 永远走事件化路径

目标态下：

1. `ScriptRuntimeGAgent` 收到 `RunScriptRequestedEvent` 后，总是先登记 pending definition query 事实。
2. definition snapshot 总是通过 typed query event / reply stream 返回。
3. activation 恢复、timeout 对账、重复响应去重都走同一套逻辑。

结果是：

1. Local / Orleans 不再有两条执行语义。
2. runtime 行为不再依赖类型名猜测。
3. `DefaultScriptingRuntimeQueryModes` 可以删除，或退化为显式测试/实验开关而非生产语义。

### 6.2 completion observation 永远走 projection-first

目标态下：

1. live completion 继续走 `ScriptEvolutionSessionCompletedEventProjector -> ProjectionSessionEventHub`。
2. durable completion 不再回 query session actor，而是读取和 live path 同源的 projection/read side 事实。
3. application/interaction 层只依赖“session completion event 的投影结果”，不再在 timeout 后换一套观测机制。

推荐落点：

1. 为 `ScriptEvolutionInteractionCompletion` 增加 projection-backed read port。
2. 让 `ScriptEvolutionDurableCompletionResolver` 改为查询 `ScriptEvolutionReadModel` 或等价 projection store。
3. `QueryScriptEvolutionDecisionRequestedEvent` / `ScriptEvolutionDecisionRespondedEvent` 退出外部 interaction 主链。

### 6.3 timeout 与策略配置收窄

目标态下：

1. definition query timeout、catalog query timeout、interaction completion timeout 使用职责清晰的 typed options。
2. 不再用一个 `IScriptingPortTimeouts` 承载所有端口常量。
3. runtime query mode 不再通过 `IScriptingRuntimeQueryModes` 暴露到业务主链。

## 7. 执行顺序

严格按以下顺序推进：

1. 先收敛 completion 事实源，确定 read-side 权威路径。
2. 再删除 interaction 主链里的 actor query fallback。
3. 再统一 definition snapshot 的事件化路径。
4. 最后拆 timeout / mode 配置，补测试和文档。

不要一开始同时重写 runtime actor、projection store、generic interaction 框架三条链。

## 8. 任务清单

## T1. completion 事实源回到 projection / read side

### 目标

让 script evolution completion 的 live 与 durable 观察共享同一个权威事实源。

### 任务

1. 盘点当前 `ScriptEvolutionSessionCompletedEventProjector`、`ScriptEvolutionReadModelProjector`、`ProjectionSessionEventHub` 的职责边界。
2. 定义 completion read port，明确读取键、返回模型与 terminal completion 判定规则。
3. 明确 completion durable resolver 读取 projection/read side，而不是 query actor。

### 涉及位置

1. `src/Aevatar.Scripting.Projection/Projectors/ScriptEvolutionSessionCompletedEventProjector.cs`
2. `src/Aevatar.Scripting.Projection/Projectors/ScriptEvolutionReadModelProjector.cs`
3. `src/Aevatar.Scripting.Infrastructure/Ports/ScriptEvolutionDurableCompletionResolver.cs`
4. `src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptEvolutionDecisionFallbackPort.cs`

### 验收

1. timeout 后的 completion 判定与 live path 使用同源事实。
2. 主链不再 fallback 到 actor query。

## T2. 删除 evolution interaction 主链中的 actor query fallback

### 目标

让 `RuntimeScriptEvolutionInteractionService` 只依赖 generic interaction + projection observation。

### 任务

1. 收紧 `ScriptEvolutionDurableCompletionResolver` 的职责。
2. 将 `RuntimeScriptEvolutionDecisionFallbackPort` 从主链删除，或降级为显式 admin/debug service。
3. 梳理 `QueryScriptEvolutionDecisionRequestedEvent` / `ScriptEvolutionDecisionRespondedEvent` 是否仍需保留在契约层。

### 涉及位置

1. `src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptEvolutionInteractionService.cs`
2. `src/Aevatar.Scripting.Infrastructure/Ports/ScriptEvolutionDurableCompletionResolver.cs`
3. `src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptEvolutionDecisionFallbackPort.cs`
4. `src/Aevatar.Scripting.Abstractions/script_host_messages.proto`

### 验收

1. interaction timeout 不再通过 actor query 二次判定完成态。
2. completion error/timeout 语义与 ACK honesty 一致。

## T3. definition snapshot 路径统一为事件化单主线

### 目标

让 `ScriptRuntimeGAgent` 不再依赖 mode switch 决定 definition 获取方式。

### 任务

1. 删除 `UseEventDrivenDefinitionQuery` 在业务主链中的分支。
2. 删除 `DefaultScriptingRuntimeQueryModes` 里的 runtime type fallback。
3. `ScriptRuntimeGAgent` 永远登记 pending query、永远走 response/timeout/recovery 主线。

### 涉及位置

1. `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs`
2. `src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptDefinitionSnapshotPort.cs`
3. `src/Aevatar.Scripting.Infrastructure/Ports/DefaultScriptingRuntimeQueryModes.cs`
4. `src/Aevatar.Scripting.Hosting/DependencyInjection/ServiceCollectionExtensions.cs`

### 验收

1. Local / Orleans 不再有 definition 获取双路径。
2. activation 恢复与 timeout 对账回归继续通过。

## T4. timeout / query mode 配置收窄

### 目标

把“大口袋式端口超时/模式配置”收敛为 typed options。

### 任务

1. 拆分 `IScriptingPortTimeouts`。
2. 删除或收窄 `IScriptingRuntimeQueryModes`。
3. 在 Hosting 层提供按职责分组的 options 绑定。

### 涉及位置

1. `src/Aevatar.Scripting.Infrastructure/Ports/IScriptingPortTimeouts.cs`
2. `src/Aevatar.Scripting.Infrastructure/Ports/DefaultScriptingPortTimeouts.cs`
3. `src/Aevatar.Scripting.Infrastructure/Ports/IScriptingRuntimeQueryModes.cs`
4. `src/Aevatar.Scripting.Hosting/DependencyInjection/ServiceCollectionExtensions.cs`

### 验收

1. 业务主链不再感知 runtime mode 开关。
2. timeout 配置名与职责一一对应。

## T5. 测试、门禁与文档收口

### 目标

确保第三阶段不会在“主链正确、fallback 偷活”这种位置回退。

### 任务

1. 补 definition query 单主线回归测试。
2. 补 projection-first completion durable resolver 测试。
3. 补 external evolution / autonomous evolution / mixed-version 回归。
4. 更新 `docs/SCRIPTING_ARCHITECTURE.md` 与必要 guards。

### 验收

1. 读写分离、projection-first completion 与 runtime-neutral query mode 由测试/guard 共同约束。

## 9. 验收命令

本阶段实际执行：

1. `dotnet build aevatar.slnx --nologo`
2. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --collect:"XPlat Code Coverage"`
3. `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --nologo --filter "FullyQualifiedName~ScriptCapabilityHostExtensionsTests"`
4. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~ClaimScriptDocumentDrivenFlexibilityTests"`
5. `dotnet test test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj --nologo --filter "FullyQualifiedName=Aevatar.Integration.Tests.ScriptExternalEvolutionE2ETests.ExternalEvolutionFlow_ShouldPromoteRevisionThroughUnifiedManagerChain"`
6. `AEVATAR_TEST_ORLEANS_3NODE=1 dotnet test test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj --nologo --filter "FullyQualifiedName=Aevatar.Integration.Tests.ScriptAutonomousEvolutionOrleans3ClusterConsistencyTests.ComplexScriptFlow_ShouldRemainConsistentAcrossThreeOrleansSilos"`
7. `bash tools/ci/distributed_mixed_version_smoke.sh`
8. `bash tools/ci/architecture_guards.sh`
9. `bash tools/ci/test_stability_guards.sh`
10. `dotnet test test/Aevatar.Foundation.Core.Tests/Aevatar.Foundation.Core.Tests.csproj --nologo --collect:"XPlat Code Coverage"`
11. `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --collect:"XPlat Code Coverage" --filter "FullyQualifiedName~OrleansGrainEventPublisherTests|FullyQualifiedName~RuntimePersistenceAndRoutingCoverageTests"`
12. `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --collect:"XPlat Code Coverage" --filter "FullyQualifiedName~OrleansRuntimeActorStateStoreIntegrationTests"`

## 10. 收束性结论

第三阶段不应该再围绕“actor execution owner 是谁”打转；第二阶段已经把这件事解决了。  

当前真正需要解决的是：

1. definition snapshot 仍有 mode switch
2. evolution completion 仍有 observation 双轨
3. runtime 行为配置仍有总口袋 abstraction

第三阶段完成后，`Scripting` 的运行查询与完成态观察都应回到单一主链：

`typed query / projection observation / read-side durable completion / runtime-neutral behavior`

## 11. 实施结果（2026-03-12）

### 已完成落点

1. `ScriptRuntimeGAgent` 已删除 `UseEventDrivenDefinitionQuery` 分支，definition snapshot 永远先登记 pending query，再走 typed query / reply 主线。
2. `IScriptingRuntimeQueryModes`、`DefaultScriptingRuntimeQueryModes`、`ScriptingRuntimeQueryModeOptions` 已删除。
3. `IScriptingPortTimeouts` 已拆为 `ScriptingQueryTimeoutOptions` 与 `ScriptingInteractionTimeoutOptions`。
4. `RuntimeScriptEvolutionDecisionFallbackPort`、`IScriptEvolutionDecisionFallbackPort`、`QueryScriptEvolutionDecisionRequestedEvent`、`ScriptEvolutionDecisionRespondedEvent` 已从主链删除。
5. `ScriptEvolutionDurableCompletionResolver` 已改为依赖 `IScriptEvolutionDecisionReadPort`，其默认实现 `ProjectionScriptEvolutionDecisionReadPort` 直接读取 `ScriptEvolutionReadModel`。
6. `ScriptingQueryEnvelopeFactory` / `ScriptingQueryChannels` 只保留 definition / catalog query 语义。
7. `ScriptEvolutionReadModelProjector` 已并入 `ScriptEvolutionSessionProjectionContext` 主链，并补齐 `IProjectionClock` / in-memory read store 默认注册。
8. Foundation 事件溯源提交后会统一把 committed domain event 以 `ObserveRoute` 写入 actor 可观察流，projection / live sink 由此不再依赖 actor query fallback 猜测终态。

### 验证结果

1. `dotnet build aevatar.slnx --nologo`：通过。
2. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --collect:"XPlat Code Coverage"`：`164/164` 通过。
3. `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --nologo --filter "FullyQualifiedName~ScriptCapabilityHostExtensionsTests"`：`3/3` 通过。
4. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~ClaimScriptDocumentDrivenFlexibilityTests"`：`5/5` 通过。
5. `dotnet test test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj --nologo --filter "FullyQualifiedName=Aevatar.Integration.Tests.ScriptExternalEvolutionE2ETests.ExternalEvolutionFlow_ShouldPromoteRevisionThroughUnifiedManagerChain"`：`1/1` 通过。
6. `AEVATAR_TEST_ORLEANS_3NODE=1 dotnet test test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj --nologo --filter "FullyQualifiedName=Aevatar.Integration.Tests.ScriptAutonomousEvolutionOrleans3ClusterConsistencyTests.ComplexScriptFlow_ShouldRemainConsistentAcrossThreeOrleansSilos"`：`1/1` 通过。
7. `bash tools/ci/distributed_mixed_version_smoke.sh`：通过。
8. `bash tools/ci/architecture_guards.sh`：通过。
9. `bash tools/ci/test_stability_guards.sh`：通过。
10. `dotnet test test/Aevatar.Foundation.Core.Tests/Aevatar.Foundation.Core.Tests.csproj --nologo --collect:"XPlat Code Coverage"`：`145/145` 通过。
11. `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --collect:"XPlat Code Coverage" --filter "FullyQualifiedName~OrleansGrainEventPublisherTests|FullyQualifiedName~RuntimePersistenceAndRoutingCoverageTests"`：`14/14` 通过。
12. `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --collect:"XPlat Code Coverage" --filter "FullyQualifiedName~OrleansRuntimeActorStateStoreIntegrationTests"`：`2/2` 通过。

### 覆盖率摘录

1. `test/Aevatar.Scripting.Core.Tests/TestResults/22d70bbf-5cd8-4259-97eb-a40287b3e19a/coverage.cobertura.xml`
   - `Aevatar.Scripting.Core` package line-rate：`87.19%`
   - `ScriptEvolutionSessionGAgent`：`96.46%`
   - `ScriptRuntimeGAgent`：`93.57%`
   - `ScriptEvolutionDurableCompletionResolver`：`100%`
   - `ProjectionScriptEvolutionDecisionReadPort`：`100%`
2. `test/Aevatar.Foundation.Core.Tests/TestResults/9bcd56a4-d819-4323-9733-1fea7b67b572/coverage.cobertura.xml`
   - `Aevatar.Foundation.Core` package line-rate：`78.45%`
   - `EventRouter`：`100%`
   - `GAgentBase<TState>`：`90.38%`
3. `test/Aevatar.Foundation.Runtime.Hosting.Tests/TestResults/9e6cbf41-8982-4cb2-86ad-c38e3c75c71a/coverage.cobertura.xml`
   - `OrleansGrainEventPublisher`：`100%`
4. `test/Aevatar.Foundation.Runtime.Hosting.Tests/TestResults/ffd5a882-e32a-4c99-8723-1b9c0ff26986/coverage.cobertura.xml`
   - `RuntimeActorGrain`：`62.66%`
   - `HandleEnvelopeAsyncCore`：`55%`
