# Runtime Phase-6 Final Authority Hardening / Shell Thinning 重构蓝图（Delivered, Breaking Change）

## 1. 文档元信息

1. 状态：`Delivered`
2. 版本：`v2`
3. 日期：`2026-03-07`
4. 决策级别：`Architecture Breaking Change`
5. 适用范围：
   - `src/workflow/Aevatar.Workflow.Core`
   - `src/workflow/Aevatar.Workflow.Application*`
   - `src/workflow/Aevatar.Workflow.Infrastructure*`
   - `src/workflow/Aevatar.Workflow.Host.Api`
   - `src/Aevatar.Scripting.*`
   - `src/Aevatar.Foundation.Abstractions/Connectors`
   - `demos/Aevatar.Demos.Workflow*`
   - `docs/WORKFLOW.md`
   - `docs/SCRIPTING_ARCHITECTURE.md`
   - `test/Aevatar.Workflow.*`
   - `test/Aevatar.Scripting.*`
   - `test/Aevatar.Integration.Tests`
6. 非范围：
   - mixed-version 升级验证链删除或弱化
   - Orleans durable callback reminder-only 主策略回退
   - CQRS projection 主协议重写
   - AI provider failover 产品语义调整
7. 本版结论：
   - phase-6 已交付：`WorkflowRunGAgent` shell 当前为 `389` 行，step family 细节已继续下沉到 runtime 协作者；`ScriptEvolutionSessionGAgent` 主文件压到 `74` 行并由 `ScriptEvolutionExecutionCoordinator` 承担执行编排。
   - formal host 已切到 actor-backed workflow definition catalog；connector 已从 mutable registry 改成 `IConnectorCatalog + StaticConnectorCatalog + AddConfiguredConnectorCatalog()`；活跃文档已同步到当前边界。

## 2. 背景

截至 `2026-03-07`，仓库已完成这些重要收口：

1. `WorkflowGAgent` 已收敛为 definition actor。
2. `WorkflowRunGAgent` 已成为单 run 持久事实源，并拆成多份 partial slice。
3. `Workflow` 主链已经摆脱旧 `IEventModuleFactory` 回流口。
4. `ScriptRuntimeGAgent` 已拆成 ingress / definition-query / completion / state-transition 几个边界。
5. `ScriptEvolutionManagerGAgent` 已删除，proposal 生命周期 owner 已收敛到 `ScriptEvolutionSessionGAgent`。
6. `RuntimeActorGrain` 已拆出 `RuntimeEnvelopePipeline` 及 dedup / retry / forwarding / compatibility hook 协作者。
7. `Workflow` Application 不再默认注册 `InMemoryWorkflowDefinitionCatalog`。
8. mixed-version 升级验证链已恢复并保留。

对应已交付蓝图：

1. `docs/architecture/workflow-runtime-actorized-run-persistent-state-refactor-blueprint-2026-03-07.md`
2. `docs/architecture/workflow-runtime-phase2-full-decoupling-refactor-blueprint-2026-03-07.md`
3. `docs/architecture/runtime-phase3-final-architecture-debt-elimination-blueprint-2026-03-07.md`
4. `docs/architecture/runtime-phase4-core-decomposition-and-final-cleanup-blueprint-2026-03-07.md`
5. `docs/architecture/runtime-phase5-runtime-definition-catalog-and-core-decomposition-blueprint-2026-03-07.md`

phase-6 不是继续开新主链，而是把剩余“结构热点”和“语义漏口”一次压平。

## 3. 交付前问题快照

### P1. `WorkflowRunGAgent` 仍然过厚

当前核心切片行数大致为：

1. `WorkflowRunGAgent.cs`：`389`
2. `WorkflowRunGAgent.Infrastructure.cs`：`177`
3. `WorkflowRunCallbackRuntime.cs`：`263`
4. `WorkflowRunStatefulCompletionRuntime.cs`：`366`
5. `WorkflowRunCompositionRuntime.cs`：`358`
6. `WorkflowRunAIRuntime.cs`：`347`
7. `WorkflowRunControlFlowRuntime.cs`：`244`
8. `WorkflowRunDispatchRuntime.cs`：`192`
9. `WorkflowRunHumanInteractionRuntime.cs`：`73`
10. `WorkflowRunAsyncPolicyRuntime.cs`：`154`

问题本质：

1. shell 已经不再是单文件，但仍同时掌握 run validation、primitive dispatch、callback reconcile、stateful completion、变量解析和 effect 组装。
2. 新原语虽然不再走 `StepRequests.cs`，但仍可能继续向 run owner 扩散 helper 和 branch logic。
3. `WorkflowRunGAgent` 还没有彻底退回到“owner + reducer + orchestration boundary”。

### P2. `ScriptEvolutionSessionGAgent` 仍不是薄 owner

当前：

1. `ScriptEvolutionSessionGAgent.cs` 约 `388` 行。
2. `RuntimeScriptEvolutionFlowPort.cs` 仍同时承担 compile、catalog query、definition upsert、promotion、compensation。
3. `RuntimeScriptEvolutionLifecycleService.cs` 已不再 fallback，但 session owner 仍直接触发大块执行编排。

问题本质：

1. session actor 还同时承载 ingress、decision query response、执行触发、终态持久化。
2. “proposal 生命周期 owner” 和 “execution coordinator” 边界仍然耦合。
3. scripting 侧还没有形成和 workflow run 相同层次的 thin owner 结构。

### P3. 正式宿主仍直接选择 `InMemoryWorkflowDefinitionCatalog`

当前：

1. `src/workflow/Aevatar.Workflow.Host.Api/Program.cs` 直接调用 `AddInMemoryWorkflowDefinitionCatalog()`。
2. `demos/Aevatar.Demos.Workflow.Web/Program.cs` 也直接调用 `AddInMemoryWorkflowDefinitionCatalog()`。
3. `demos/Aevatar.Demos.Workflow/Program.cs` 仍直接 `new InMemoryWorkflowDefinitionCatalog()`。

问题本质：

1. Application 层默认 fact source 泄漏已经修掉，但 Host 侧仍把 dev-only catalog 当成正式启动路径。
2. 这会让部署语义继续依赖进程内 definition fact source。
3. 名称上叫 `Host.Api`，实际却默认 dev catalog，会误导使用者对正式部署语义的判断。

### P4. connector 仍表达成可变注册表（交付前）

当前：

1. connector 抽象暴露了 `Register / TryGet / ListNames` 可变语义。
2. 默认本地实现仍是 `lock + Dictionary`。
3. Infrastructure 默认把它注册成 singleton。

问题本质：

1. 这套 API 看起来像“运行期权威事实源”，但实现只是本地进程注册表。
2. 如果 connector 语义本质上是启动期配置，就不该建模成可变 registry。
3. 如果 connector 将来需要跨节点一致性，那当前抽象也不够硬，应该直接上 actor/distributed catalog，而不是继续挂本地锁表。

### P5. `WorkflowValidator` 仍是单体静态校验器

当前：

1. `WorkflowParser` 和 `WorkflowExpressionEvaluator` 已经拆边界。
2. `WorkflowValidator.cs` 仍有约 `254` 行，继续聚合结构、graph、primitive 参数、可达性等多种规则。
3. `WorkflowGAgent`、`WorkflowRunGAgent`、`WorkflowRunActorPort`、demo 都直接调这个静态入口。

问题本质：

1. DSL 的 parse / normalize 已拆，但 validate 还未拆成 rule sets。
2. 校验规则新增时仍会回到大 switch / 大 helper 集中堆叠。
3. demo 和 production 路径复用了同一静态入口，缺少更清晰的 validation service boundary。

### P6. 活跃文档和 demo 仍有少量旧口径

当前：

1. `docs/WORKFLOW.md` 和 `src/workflow/Aevatar.Workflow.Core/README.md` 仍提到已删除的旧 step-request 切片。
2. demo 程序仍内嵌较多“dev-only composition 但看起来像正式架构”的做法。
3. 部分历史架构文档仍保留交付前证据，但活跃文档还需进一步避免把过时切片或实现口径写成当前事实。

问题本质：

1. 当前代码已经前进，但说明文档未完全收口。
2. 长期看，文档漂移会把已经删除的边界重新带回团队心智模型。

## 4. 终局目标

phase-6 完成后，系统应满足下面 6 条终局约束：

1. `WorkflowRunGAgent` 只保留 run owner、state transition、orchestration boundary，不再直接承载大块 primitive family 细节。
2. `ScriptEvolutionSessionGAgent` 只保留 session owner、query authority、result persistence，不再直接承担 execution orchestration。
3. 正式 Host 不再默认依赖 `InMemoryWorkflowDefinitionCatalog`；definition 权威事实源必须显式选择 actor-backed 或 distributed-backed 实现。
4. connector 不再被建模成“运行期可变 registry”；如果只是启动期配置，则变成 immutable catalog/provider；如果未来需要跨节点动态注册，必须另起 actorized catalog。
5. Workflow DSL 的 parse / normalize / validate / evaluate 四层边界全部独立。
6. 活跃文档、README、guards 与现实现状一致，不再引用删除后的切片或历史兼容语义。

## 5. 目标架构

### 5.1 Workflow Run

1. `WorkflowRunGAgent`
   - 只负责：state owner、domain-event persistence、dispatch 入口、result commit/fail。
2. `WorkflowRunReducer`
   - 只负责 run state transition。
3. `WorkflowPrimitiveDispatchTable`
   - 只负责 canonical primitive -> handler family route。
4. `WorkflowPrimitiveFamilyHandlers`
   - `ControlFlow`
   - `HumanInteraction`
   - `AI`
   - `Composition`
5. `WorkflowRunEffectAssembler`
   - 负责从 handler 输出组装 effect plan。
6. `WorkflowRunEffectDispatcher`
   - 负责执行 send / publish / callback / child-run / connector invocation。
7. `WorkflowAsyncOperationReconciler`
   - 继续作为 callback/async completion 唯一对账入口。

### 5.2 Script Evolution

1. `ScriptEvolutionSessionGAgent`
   - 只负责 proposal 生命周期 owner、query authority、completed event persistence。
2. `ScriptEvolutionSessionReducer`
   - 只负责 state transition。
3. `ScriptEvolutionExecutionCoordinator`
   - 负责 compile、catalog load、definition upsert、promotion、compensation。
4. `ScriptEvolutionEffectDispatcher`
   - 负责把 coordinator 结果映射成 session domain events。
5. `RuntimeScriptEvolutionLifecycleService`
   - 保持 `dispatch session actor -> query session actor decision`，不回退到 projection 或 fallback path。

### 5.3 Workflow Definition Catalog

1. 正式路径：
   - `IWorkflowDefinitionCatalog`
   - actor-backed / distributed-backed implementation
2. dev/test/demo 路径：
   - `AddInMemoryWorkflowDefinitionCatalog()`
   - 仅允许在显式 dev/demo 组合中使用
3. `Aevatar.Workflow.Host.Api`
   - 启动时若未注册正式 catalog，则 fail fast

### 5.4 Connectors

1. 当前系统选择单一路径：connector 视为启动期静态配置，而不是运行期事实源。
2. 删除“可变 registry”心智模型，改成 immutable catalog/provider。
3. 若未来真要支持跨节点动态 connector 注册，另起 `ConnectorCatalogGAgent`，不在本 phase 混入。

### 5.5 Workflow Validation

1. `WorkflowDefinitionStaticValidator`
   - 结构合法性
2. `WorkflowGraphValidator`
   - graph / reachability / branch target
3. `WorkflowPrimitiveParameterValidator`
   - primitive 参数约束
4. `WorkflowValidationService`
   - 聚合各 rule set，提供统一结果

## 6. 详细重构设计

### 6.1 WP1: `WorkflowRunGAgent` 最终去壳化

#### 6.1.1 目标

让 `WorkflowRunGAgent` 从“多文件大 orchestrator”收敛成真正的 run owner。

#### 6.1.2 破坏性决策

1. `WorkflowRunGAgent` 中不得继续新增 primitive-specific private methods。
2. `Infrastructure / Callbacks / StatefulCompletions` 中的 family-specific 逻辑全部继续外提。
3. shell 只保留：
   - 事件入口
   - state transition 调用
   - effect dispatch 触发
   - async reconcile 入口

#### 6.1.3 新结构

1. `WorkflowRunReducer.cs`
2. `WorkflowRunEffectAssembler.cs`
3. `WorkflowControlFlowHandler.cs`
4. `WorkflowHumanInteractionHandler.cs`
5. `WorkflowAIHandler.cs`
6. `WorkflowCompositionHandler.cs`
7. `WorkflowPrimitiveDispatchTable.cs`

#### 6.1.4 主要影响文件

1. `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs`
2. `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.Infrastructure.cs`
3. `src/workflow/Aevatar.Workflow.Core/WorkflowRunCallbackRuntime.cs`
4. `src/workflow/Aevatar.Workflow.Core/WorkflowRunStatefulCompletionRuntime.cs`
5. `src/workflow/Aevatar.Workflow.Core/WorkflowRunDispatchRuntime.cs`
6. `src/workflow/Aevatar.Workflow.Core/WorkflowRunHumanInteractionRuntime.cs`
7. `src/workflow/Aevatar.Workflow.Core/WorkflowPrimitiveExecutionPlanner.cs`
8. `test/Aevatar.Workflow.Core.Tests/*`
9. `test/Aevatar.Integration.Tests/*Workflow*`

#### 6.1.5 验收标准

1. `WorkflowRunGAgent.cs` 控制在 `400` 行以内。
2. `WorkflowRunGAgent.*` 任一切片不再兼具 family dispatch 与 state transition 逻辑。
3. primitive family 新增能力只能挂入 family handler / dispatch table。
4. callback / completion 仍只通过 reconcile 入口收敛。

### 6.2 WP2: `ScriptEvolutionSessionGAgent` 薄 owner 化

#### 6.2.1 目标

让 session actor 与 workflow run actor 处于同等抽象层级：只做 owner，不做 execution monolith。

#### 6.2.2 破坏性决策

1. `IScriptEvolutionFlowPort` 重命名或收窄为真正的 coordinator 契约。
2. `ScriptEvolutionSessionGAgent` 不再直接写大段 execute branch。
3. decision query 只读 session state，不再隐式带入任何 runtime fallback。

#### 6.2.3 新结构

1. `ScriptEvolutionSessionReducer`
2. `ScriptEvolutionExecutionCoordinator`
3. `ScriptEvolutionEffectDispatcher`
4. `ScriptEvolutionDecisionResponder`

#### 6.2.4 主要影响文件

1. `src/Aevatar.Scripting.Core/ScriptEvolutionSessionGAgent.cs`
2. `src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptEvolutionFlowPort.cs`
3. `src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptEvolutionLifecycleService.cs`
4. `test/Aevatar.Scripting.Core.Tests/Runtime/*ScriptEvolution*`
5. `test/Aevatar.Integration.Tests/*ScriptAutonomousEvolution*`
6. `docs/SCRIPTING_ARCHITECTURE.md`

#### 6.2.5 验收标准

1. `ScriptEvolutionSessionGAgent.cs` 控制在 `220` 行以内。
2. compile / promotion / compensation 不再与 query response 混在同一 actor 文件。
3. `RuntimeScriptEvolutionLifecycleService` 继续只保留 `dispatch + query`。
4. 整条 proposal 流程不再依赖任何 fallback 语义。

### 6.3 WP3: 正式 Host 的 definition authority 硬化

#### 6.3.1 目标

让正式宿主停止默认依赖 in-memory definition catalog。

#### 6.3.2 破坏性决策

1. `Aevatar.Workflow.Host.Api` 不再直接调用 `AddInMemoryWorkflowDefinitionCatalog()`。
2. `demos/Aevatar.Demos.Workflow/Program.cs` 不再直接 `new InMemoryWorkflowDefinitionCatalog()` 作为通用示例写法。
3. 正式 Host 若未注册 catalog authority，则启动失败。

#### 6.3.3 新模型

1. `AddActorBackedWorkflowDefinitionCatalog()` 或 `AddDistributedWorkflowDefinitionCatalog()`
2. `AddInMemoryWorkflowDefinitionCatalog()` 仅 dev/test/demo 显式使用
3. `Workflow.Host.Api` 对 catalog registration 做 startup validation

#### 6.3.4 主要影响文件

1. `src/workflow/Aevatar.Workflow.Host.Api/Program.cs`
2. `src/workflow/Aevatar.Workflow.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
3. `demos/Aevatar.Demos.Workflow.Web/Program.cs`
4. `demos/Aevatar.Demos.Workflow/Program.cs`
5. `test/Aevatar.Workflow.Host.Api.Tests/*`
6. `docs/WORKFLOW.md`

#### 6.3.5 验收标准

1. 非 test/demo 正式 Host 代码中不再出现 `AddInMemoryWorkflowDefinitionCatalog()`。
2. 正式启动路径必须显式绑定分布式 definition authority。
3. 任何未配置 authority 的正式 Host 都会 fail fast，而不是悄悄退回进程内 catalog。

### 6.4 WP4: connector 语义硬化为 immutable catalog

#### 6.4.1 目标

彻底消除“本地 mutable registry 冒充运行期事实源”的歧义。

#### 6.4.2 破坏性决策

1. 删除 connector 抽象中的 `Register(...)` 这种可变语义。
2. 将 connector 主语义收敛为启动期 immutable provider/catalog。
3. 旧的本地可变实现删除，改成明确的静态实现，如 `StaticConnectorCatalog`。

#### 6.4.3 新模型

1. `IConnectorCatalog`
   - `TryGet`
   - `ListNames`
2. `IConnectorCatalogBuilder`
   - 仅启动期组合使用
3. `StaticConnectorCatalog`
   - immutable snapshot implementation

#### 6.4.4 主要影响文件

1. `src/Aevatar.Foundation.Abstractions/Connectors/IConnector.cs`
2. `src/Aevatar.Foundation.Abstractions/Connectors/IConnector.cs`
3. `src/workflow/Aevatar.Workflow.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
4. 任何直接调用 `Register` 的 test/demo 代码
5. `docs/CONNECTOR.md`

#### 6.4.5 验收标准

1. 生产语义下不再存在 mutable connector registry。
2. Infrastructure 默认实现不再依赖 `lock + Dictionary` 注册表模型。
3. connector 若只是启动期配置，其 API 形态也必须表达“静态快照”。

### 6.5 WP5: Workflow validation service 化

#### 6.5.1 目标

把 `WorkflowValidator` 从单体静态类拆成多个 rule sets 与统一 service。

#### 6.5.2 破坏性决策

1. `WorkflowValidator.Validate(...)` 不再承载所有规则实现。
2. runtime / host / demo 不再直接依赖大静态 helper。
3. 新增 DSL 校验规则必须先落到独立 validator，再由 service 聚合。

#### 6.5.3 新结构

1. `WorkflowDefinitionStaticValidator`
2. `WorkflowGraphValidator`
3. `WorkflowPrimitiveParameterValidator`
4. `WorkflowValidationService`
5. `WorkflowValidationResultComposer`

#### 6.5.4 主要影响文件

1. `src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs`
2. `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`
3. `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs`
4. `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs`
5. `src/workflow/Aevatar.Workflow.Core/WorkflowYamlValidationSupport.cs`
6. `demos/Aevatar.Demos.Workflow.Web/Program.cs`
7. `test/Aevatar.Workflow.Core.Tests/*Validator*`

#### 6.5.5 验收标准

1. graph / structure / primitive 参数校验拥有独立测试文件。
2. `WorkflowValidator.cs` 若保留，只能作为 facade。
3. 新 DSL 规则无需再向单一静态类追加大块条件分支。

### 6.6 WP6: 活跃文档与 demo 口径彻底收口

#### 6.6.1 目标

消除活跃文档与 demo 中对旧切片、旧默认语义的残留引用。

#### 6.6.2 破坏性决策

1. 活跃 README / WORKFLOW 文档不再引用已删除的旧 step-request 切片。
2. demo 若使用 dev-only 组合，必须显式标明 `development-only`。
3. 历史架构文档保留证据，但活跃文档只描述当前语义。

#### 6.6.3 主要影响文件

1. `docs/WORKFLOW.md`
2. `docs/SCRIPTING_ARCHITECTURE.md`
3. `src/workflow/Aevatar.Workflow.Core/README.md`
4. `src/workflow/Aevatar.Workflow.Application/README.md`
5. `demos/Aevatar.Demos.Workflow/README.md`
6. `demos/Aevatar.Demos.Workflow.Web/README.md`

#### 6.6.4 验收标准

1. 活跃文档中不再引用已删除的切片名或过时默认组合。
2. demo/dev-only 说明与正式 Host 语义明确分离。
3. guards 能阻止旧口径回流。

## 7. 实施顺序

建议按下面顺序落地：

1. `WP3`：先硬化正式 Host 的 definition authority，避免继续以 dev-only composition 作为默认正式入口。
2. `WP1`：再完成 `WorkflowRunGAgent` 的最终去壳化。
3. `WP2`：随后把 `ScriptEvolutionSessionGAgent` 压成薄 owner。
4. `WP4`：再处理 connector 语义，消除 mutable registry 模型。
5. `WP5`：最后拆 `WorkflowValidator`。
6. `WP6`：每个工作包完成后即时同步文档与 guards，不在最后一次性补。

## 8. 风险与取舍

1. `WP3` 会让当前依赖内存 catalog 的正式 Host 启动方式直接失效，这是故意选择，不保留静默 fallback。
2. `WP1` 与 `WP2` 都是“继续拆 owner shell”，风险不在语义而在边界梳理，需要用 reducer / coordinator / dispatcher 的职责测试兜住。
3. `WP4` 如果错误地继续保留可变 registry，只会把“配置”和“事实源”继续混在一起；因此这一步必须做成明确 breaking change。
4. `WP5` 对运行语义风险较低，但对 DSL 扩展长期维护价值很高，应在核心 owner 和 authority 收口之后尽快完成。

## 9. 测试矩阵

### 9.1 必跑 build / guards

1. `dotnet build aevatar.slnx --nologo`
2. `bash tools/ci/architecture_guards.sh`
3. `bash tools/ci/test_stability_guards.sh`
4. `bash tools/ci/solution_split_test_guards.sh`

### 9.2 Workflow 必跑

1. `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo`
2. `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo`
3. `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo`
4. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~Workflow"`

### 9.3 Scripting 必跑

1. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo`
2. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~ScriptAutonomousEvolution|FullyQualifiedName~ScriptExternalEvolution"`

### 9.4 Runtime 必跑

1. `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo`
2. mixed-version 相关测试与 `tools/ci/distributed_mixed_version_smoke.sh` 保持保留

### 9.5 最终闭环

1. `dotnet test aevatar.slnx --nologo`

## 10. Guard 调整

1. 禁止 `src/workflow/Aevatar.Workflow.Host.Api/**` 再出现 `AddInMemoryWorkflowDefinitionCatalog()`。
2. 禁止非 `test/` 与非 `demos/` 代码直接 `new InMemoryWorkflowDefinitionCatalog()`。
3. 禁止活跃文档继续引用已删除的旧 step-request 切片。
4. 禁止 connector 抽象继续暴露 `Register(...)`。
5. 若 `WorkflowRunGAgent` 主文件或核心切片重新显著膨胀，则 guard 直接失败。

## 11. Definition of Done

phase-6 完成时，必须同时满足：

1. `WorkflowRunGAgent` 已退回 thin owner。
2. `ScriptEvolutionSessionGAgent` 已退回 thin owner。
3. 正式 Host 不再默认依赖 in-memory definition catalog。
4. connector API 不再表达可变本地 registry。
5. `WorkflowValidator` 已拆成 rule-set + service。
6. 活跃文档与 README 完全对齐当前实现。
7. mixed-version 升级验证链仍存在且通过。
8. `build / guards / targeted tests / full solution tests` 全部通过。
