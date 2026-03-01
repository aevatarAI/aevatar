# Aevatar.Scripting 自治进化重构实施文档（2026-03-02）

## 1. 变更结论

本次重构已从“执行型脚本框架”升级为“执行 + 演化”框架，并完成“双通道终态”的落地：

1. 脚本可在运行时自建临时脚本 Runtime。
2. 脚本可在运行时创建新脚本定义并运行新脚本 Runtime。
3. 脚本可在运行时发起升级提案并完成验证与发布。
4. 发布事实由 `ScriptEvolutionManagerGAgent + ScriptCatalogGAgent` 持久化维护。
5. 外部更新入口（API/CI/Ops）已通过标准化 Host/API + Application Service 接入并走同一治理主链路。

## 2. 关键架构变更

### 2.1 Abstractions

新增文件：

1. `src/Aevatar.Scripting.Abstractions/Definitions/ScriptEvolutionProposal.cs`
2. `src/Aevatar.Scripting.Abstractions/Definitions/ScriptEvolutionValidationReport.cs`
3. `src/Aevatar.Scripting.Abstractions/Definitions/ScriptPromotionDecision.cs`

扩展接口：

1. `src/Aevatar.Scripting.Abstractions/Definitions/IScriptRuntimeCapabilities.cs`

扩展 proto：

1. `src/Aevatar.Scripting.Abstractions/script_host_messages.proto`

新增 `EvolutionManager/Catalog` 状态和演化事件链。

### 2.2 Core

新增 Actor：

1. `src/Aevatar.Scripting.Core/ScriptEvolutionManagerGAgent.cs`
2. `src/Aevatar.Scripting.Core/ScriptCatalogGAgent.cs`

新增端口：

1. `src/Aevatar.Scripting.Core/Ports/IScriptEvolutionPort.cs`
2. `src/Aevatar.Scripting.Core/Ports/IScriptEvolutionFlowPort.cs`
3. `src/Aevatar.Scripting.Core/Ports/IScriptPolicyGatePort.cs`
4. `src/Aevatar.Scripting.Core/Ports/IScriptValidationPipelinePort.cs`
5. `src/Aevatar.Scripting.Core/Ports/IScriptPromotionPort.cs`
6. `src/Aevatar.Scripting.Core/Ports/IScriptCatalogPort.cs`
7. `src/Aevatar.Scripting.Core/Ports/IScriptDefinitionLifecyclePort.cs`
8. `src/Aevatar.Scripting.Core/Ports/IScriptRuntimeLifecyclePort.cs`
9. `src/Aevatar.Scripting.Core/Ports/IScriptingActorAddressResolver.cs`

新增快照源接口：

1. `src/Aevatar.Scripting.Core/IScriptEvolutionDecisionSource.cs`
2. `src/Aevatar.Scripting.Core/IScriptCatalogSnapshotSource.cs`

### 2.3 Application

新增命令适配器：

1. `ProposeScriptEvolutionCommand(+Adapter)`
2. `PromoteScriptRevisionCommand(+Adapter)`
3. `RollbackScriptRevisionCommand(+Adapter)`

运行编排器增强：

1. `src/Aevatar.Scripting.Application/Runtime/ScriptRuntimeExecutionOrchestrator.cs`
2. `src/Aevatar.Scripting.Application/Runtime/ScriptRuntimeCapabilityComposer.cs`
3. `src/Aevatar.Scripting.Core/Runtime/ScriptRuntimeCapabilities.cs`

能力上下文已拆分为 `Interaction/Lifecycle/Evolution` 三类 capability 并通过 composer 组装。

新增应用服务（外部入口统一编排）：

1. `src/Aevatar.Scripting.Application/Application/IScriptEvolutionApplicationService.cs`
2. `src/Aevatar.Scripting.Application/Application/ProposeScriptEvolutionRequest.cs`
3. `src/Aevatar.Scripting.Application/Application/ScriptEvolutionApplicationService.cs`

### 2.4 Hosting

新增端口实现：

1. `DefaultScriptingActorAddressResolver`
2. `RuntimeScriptEvolutionPort`
3. `RuntimeScriptEvolutionFlowPort`
4. `RuntimeScriptPolicyGatePort`
5. `RuntimeScriptValidationPipelinePort`
6. `RuntimeScriptPromotionPort`
7. `RuntimeScriptCatalogPort`
8. `RuntimeScriptDefinitionLifecyclePort`
9. `RuntimeScriptRuntimeLifecyclePort`

DI 装配更新：

1. `src/Aevatar.Scripting.Hosting/DependencyInjection/ServiceCollectionExtensions.cs`

新增外部入口：

1. `src/Aevatar.Scripting.Hosting/CapabilityApi/ScriptCapabilityEndpoints.cs`
2. `src/Aevatar.Scripting.Hosting/CapabilityApi/ScriptCapabilityHostBuilderExtensions.cs`

### 2.5 Projection

新增演化审计投影：

1. `ScriptEvolutionReadModelProjector`
2. `ScriptEvolutionReadModel`
3. `ScriptEvolution*Reducer` 全链路
4. `ScriptEvolutionProjectionContext`

## 3. 设计取舍

1. 保持 `ScriptRuntimeGAgent` 作为运行入口，不新增旁路执行主干。
2. 让升级事实进入 Actor 状态（Manager/Catalog），不在中间层保留 `proposalId -> context` 字典事实态。
3. 用 `IScriptingActorAddressResolver` 统一 actor 地址命名，不在多层散落字符串常量。
4. 用 `IScriptEvolutionFlowPort` 下沉 `policy/validation/promotion` 串行流程，降低 EvolutionManager 直接依赖面。
5. 策略门禁与验证流水线通过端口抽象，Core 不依赖具体实现。
6. Catalog 维护 active revision/rollback 指针，避免发布状态散落在多个中间服务。

## 4. 脚本内场景落地

场景覆盖测试：`test/Aevatar.Integration.Tests/ScriptAutonomousEvolutionE2ETests.cs`

在单次脚本运行中完成：

1. 创建临时已定义脚本 Runtime 并运行。
2. 创建新脚本定义 + 新脚本 Runtime 并运行。
3. 提交升级提案并发布 `worker-script` 从 `rev-worker-1` 到 `rev-worker-2`。
4. 启动升级后的 Runtime 并运行。

双通道状态说明：

1. 自我演化入口：已完成实现并通过 E2E。
2. 外部更新入口：已完成 `POST /api/scripts/evolutions/proposals` 标准化入口并通过 E2E。

## 5. 验证结果

构建与测试：

1. `dotnet build aevatar.slnx --nologo` 通过。
2. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo` 通过（58/58）。
3. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~ScriptAutonomousEvolutionE2ETests|FullyQualifiedName~ScriptExternalEvolutionE2ETests"` 通过（2/2）。
4. `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --nologo --filter "FullyQualifiedName~ScriptCapabilityHostExtensionsTests"` 通过（3/3）。
5. `dotnet test aevatar.slnx --nologo` 通过。

门禁：

1. `bash tools/ci/architecture_guards.sh` 通过。
2. `bash tools/ci/projection_route_mapping_guard.sh` 通过。
3. `bash tools/ci/test_stability_guards.sh` 通过。
4. `bash tools/ci/solution_split_guards.sh` 通过。
5. `bash tools/ci/solution_split_test_guards.sh` 通过。

## 6. 当前边界

1. `RuntimeScriptPolicyGatePort` 仍为默认规则集，后续可接入更严格组织级策略。
2. `RuntimeScriptValidationPipelinePort` 当前以编译验证为主，后续可增加测试编排与灰度验证。
3. 若需跨集群审批链，可在不改 Core 的前提下替换 Hosting 端口实现。
