# Workflow.Core 能力聚合化重构蓝图

## 1. 文档元信息

1. 状态：`Delivered`
2. 版本：`v1`
3. 日期：`2026-03-08`
4. 决策级别：`Architecture Breaking Change`
5. 主范围：
   - `src/workflow/Aevatar.Workflow.Core`
   - `test/Aevatar.Workflow.Core.Tests`
   - `test/Aevatar.Workflow.Host.Api.Tests`
   - `test/Aevatar.Integration.Tests`
   - `docs/WORKFLOW.md`
   - `src/workflow/Aevatar.Workflow.Core/README.md`
6. 替代关系：
   - 本文已替代并完成 `workflow-run-runtime-suite-thin-owner-blueprint-2026-03-08.md`
   - 本文定义的 capability-oriented 方案已经落地
   - 旧 `RuntimeSuite + 宽 Context + 多 Registry` 方案已删除

## 2. 结论先行

当前 `Workflow.Core` 的主要问题不是“文件太多”或“文件太少”，而是：

1. 同一业务能力被拆散到多个技术壳中。
2. 不相关的能力又被塞进共享 `Context / Support / Patch / Suite`。
3. 新增抽象没有真正形成开闭边界，只是把复杂度横向摊开。

本次重构的目标不是继续加 `Runtime / Registry / Suite / Reconciler` 名词，而是把 `Workflow.Core` 改成：

1. `WorkflowRunGAgent = owner shell`
2. `Capability = 具体业务能力的唯一聚合边界`
3. `Capability Router = 唯一运行时分发内核`
4. `ReadContext / WriteContext / EffectPorts = 受约束的共享能力`
5. `State Patch Contributor = 每个能力各自维护自己的状态切片补丁`

一句话：

`按业务能力聚合，而不是按技术切片分散。`

## 3. 当前实现存在的结构性问题

### 3.1 Owner 仍然是 composition root

当前 [WorkflowRunGAgent.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs) 仍然负责：

1. 创建 `WorkflowExpressionEvaluator`
2. 创建 `WorkflowRunStepRequestFactory`
3. 创建 `WorkflowRunEffectDispatcher`
4. 创建 `WorkflowRunRuntimeContext`
5. 创建 `WorkflowRunRuntimeSuite`
6. 将 suite 再接到 `WorkflowPrimitiveExecutionPlanner`
7. 将 suite 再接到 `WorkflowAsyncOperationReconciler`

这意味着：

1. owner 依然知道完整运行时装配图
2. owner 依然是硬编码 wiring 点
3. 新增能力仍然容易回到修改 owner 构造器

### 3.2 RuntimeSuite 是第二个硬编码装配器

当前 [WorkflowRunRuntimeSuite.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunRuntimeSuite.cs) 依然手工：

1. `new WorkflowRunControlFlowRuntime`
2. `new WorkflowRunHumanInteractionRuntime`
3. `new WorkflowRunLlmRuntime`
4. `new WorkflowRunEvaluationRuntime`
5. `new WorkflowRunReflectRuntime`
6. `new WorkflowRunCacheRuntime`
7. `new WorkflowRunFanOutRuntime`
8. `new WorkflowRunSubWorkflowRuntime`
9. `new WorkflowRunTimeoutCallbackRuntime`
10. `new WorkflowRunAIResponseRuntime`
11. `new WorkflowRunAggregationCompletionRuntime`
12. `new WorkflowRunProgressionCompletionRuntime`
13. `new WorkflowRunAsyncPolicyRuntime`

然后再按列表顺序塞进：

1. `WorkflowStepFamilyDispatchTable`
2. `WorkflowStatefulCompletionHandlerRegistry`
3. `WorkflowInternalSignalRegistry`
4. `WorkflowResponseHandlerRegistry`
5. `WorkflowChildRunCompletionRegistry`

这不是开闭设计，只是把 owner 里的硬编码移到 suite。

### 3.3 RuntimeContext 是过宽的 service locator

当前 [WorkflowRunRuntimeContext.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunRuntimeContext.cs) 同时暴露：

1. `State`
2. `CompiledWorkflow`
3. `PersistStateAsync`
4. `PublishAsync`
5. `SendToAsync`
6. `LogWarningAsync`
7. `EnsureAgentTreeAsync`
8. `ScheduleWorkflowCallbackAsync`
9. `ResolveOrCreateSubWorkflowRunActorAsync`
10. `LinkChildAsync`
11. `CleanupChildWorkflowAsync`
12. `ResolveWorkflowYamlAsync`
13. `CreateWorkflowDefinitionBindEnvelope`
14. `CreateRoleAgentInitializeEnvelope`

这会导致：

1. 所有 runtime 都拥有几乎完整的读写与 effect 权限
2. 上下文对象没有最小权限边界
3. 业务能力间更容易隐藏耦合

### 3.4 同一业务能力被拆散

以 `llm_call` 为例，当前逻辑分散在：

1. `WorkflowRunLlmRuntime.cs`
2. `WorkflowRunAIResponseRuntime.cs`
3. `WorkflowRunRuntimeSuite.cs`
4. `WorkflowAsyncOperationReconciler.cs`
5. `WorkflowRunRuntimeContext.cs`
6. `WorkflowRunSupport.cs`

这意味着读一个能力要跨多个“技术切片”跳转，而不是在一个业务边界内完成理解。

### 3.5 路由模型不统一

当前存在三种路由模式混用：

1. `StepFamilyDispatchTable`：按 canonical step type 精确键路由
2. `StatefulCompletionHandlerRegistry / ResponseHandlerRegistry / InternalSignalRegistry`：按列表顺序逐个尝试
3. `WorkflowRunAIResponseRuntime`：把 `llm/evaluation/reflect` 再手工扇入一个 fan-in handler

这会导致：

1. 新能力的接入方式不一致
2. 运行时行为要靠隐式顺序理解
3. 难以判断“某类事件究竟由谁拥有”

### 3.6 Support 和 Patch 已经变成杂物间

当前：

1. [WorkflowRunSupport.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunSupport.cs) 混合了：
   - run reset
   - semantic generation
   - token lookup
   - timeout 解析
   - LLM failure 解析
   - score 解析
   - parent step 推导
2. [WorkflowRunStatePatchSupport.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunStatePatchSupport.cs) 把所有状态切片的 patch build/apply 逻辑揉在一个 `402` 行静态类里

这两处都违反了能力聚合原则。

### 3.7 Core 依赖暴露面过大

当前 [GlobalUsings.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/GlobalUsings.cs) 直接把：

1. `Aevatar.AI.Abstractions`
2. `Aevatar.AI.Abstractions.Agents`
3. `Aevatar.AI.Abstractions.LLMProviders`
4. `Aevatar.AI.Abstractions.ToolProviders`
5. `Aevatar.Foundation.Abstractions.Connectors`

都做成了全局 using。

这会放大三个问题：

1. Core 中任何文件都更容易无意识吸入 AI / Connector 依赖
2. 文件的真实依赖边界被弱化
3. 按能力拆分目录时，边界不容易变清楚

## 4. 设计目标

### 4.1 目标一：按能力聚合代码

每个具体业务能力应成为一个可独立理解的聚合单元。例如：

1. `llm_call`
2. `evaluate`
3. `reflect`
4. `delay / wait_signal / race / while`
5. `human_input / human_approval`
6. `parallel / foreach / map_reduce`
7. `workflow_call`
8. `cache`

每个能力的入口、状态、相关 callback/response/completion、专属 effect 应放在同一能力目录下。

### 4.2 目标二：owner 只做 owner

`WorkflowRunGAgent` 只保留：

1. actor ingress handlers
2. state transition 入口
3. capability router 调用
4. run finalization / activation republish

owner 不再负责：

1. 构造具体能力
2. 知道具体能力类名
3. 直接拼装 planner/reconciler/registry 链

### 4.3 目标三：新增能力符合开闭原则

新增一个 stateful capability 时，允许的改动范围只有：

1. 新增一个能力目录
2. 扩展 `workflow_run_state.proto`
3. 新增该能力自己的 patch contributor
4. 在注册层声明 capability descriptor

不得再要求：

1. 修改 `WorkflowRunGAgent` 字段区
2. 修改 `WorkflowRunRuntimeSuite`
3. 修改通用 `Support`
4. 修改通用 `PatchSupport`
5. 修改列表顺序以保证路由正确

### 4.4 目标四：统一路由模型

所有运行时分发统一到一个模型：

1. `step request` 路由
2. `completion` 路由
3. `internal signal` 路由
4. `response` 路由
5. `child run completion` 路由

都由 capability descriptor 显式声明，不再混用：

1. map
2. ordered list
3. 二次 fan-in 手工分发

## 5. 严格约束

### 5.1 必须遵守

1. `WorkflowRunGAgent` 不得直接 `new` 任何具体 capability。
2. `WorkflowRunGAgent` 不得直接持有任何具体 capability 字段。
3. `WorkflowRunRuntimeContext` 这种宽上下文必须拆分。
4. 任何业务能力不得把自己的 response/completion 处理外包给另一个能力壳。
5. 任何新增 stateful capability 都必须拥有自己的 patch contributor。
6. 任何运行时路由都必须是显式 descriptor 驱动。

### 5.2 明确禁止

1. 禁止继续扩充 `WorkflowRunRuntimeSuite`
2. 禁止继续扩充 `WorkflowRunSupport`
3. 禁止继续扩充 `WorkflowRunStatePatchSupport`
4. 禁止新增 `WorkflowRunRuntimeBase` / `WorkflowRunHandlerBase`
5. 禁止用注册表数组顺序表达优先级语义
6. 禁止让 `AIResponse` 这类壳替其他能力持有 response ownership
7. 禁止用 `GlobalUsings` 把 AI/Connector/Tool 依赖扩散到整个 Core

## 6. 目标架构

### 6.1 目录结构

目标结构如下：

```text
src/workflow/Aevatar.Workflow.Core/
├── Agents/
│   ├── WorkflowGAgent.cs
│   ├── WorkflowRunGAgent.cs
│   ├── WorkflowRunGAgent.Lifecycle.cs
│   └── WorkflowRunGAgent.ExternalInteractions.cs
├── Run/
│   ├── Routing/
│   │   ├── IWorkflowRunCapability.cs
│   │   ├── IWorkflowRunCapabilityDescriptor.cs
│   │   ├── WorkflowRunCapabilityRegistry.cs
│   │   ├── WorkflowRunStepRouter.cs
│   │   ├── WorkflowRunCompletionRouter.cs
│   │   ├── WorkflowRunSignalRouter.cs
│   │   ├── WorkflowRunResponseRouter.cs
│   │   └── WorkflowRunChildCompletionRouter.cs
│   ├── Context/
│   │   ├── WorkflowRunReadContext.cs
│   │   ├── WorkflowRunWriteContext.cs
│   │   └── WorkflowRunEffectPorts.cs
│   ├── State/
│   │   ├── WorkflowRunReducer.cs
│   │   ├── WorkflowRunStatePatchAssembler.cs
│   │   └── IWorkflowRunStatePatchContributor.cs
│   └── Support/
│       ├── WorkflowCorrelationKeys.cs
│       ├── WorkflowSemanticGeneration.cs
│       └── WorkflowRunIdSupport.cs
├── Capabilities/
│   ├── LlmCall/
│   ├── Evaluate/
│   ├── Reflect/
│   ├── HumanInteraction/
│   ├── ControlFlow/
│   ├── FanOut/
│   ├── SubWorkflow/
│   └── Cache/
├── PrimitiveExecutors/
├── Primitives/
├── Validation/
└── Protos/
```

### 6.2 核心抽象

#### `IWorkflowRunCapability`

一个 capability 代表一个业务能力聚合，不是一个“技术运行时”。

建议接口：

```csharp
internal interface IWorkflowRunCapability
{
    IWorkflowRunCapabilityDescriptor Descriptor { get; }

    Task HandleStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        CancellationToken ct);

    Task<bool> TryHandleCompletionAsync(
        StepCompletedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        CancellationToken ct);

    Task<bool> TryHandleInternalSignalAsync(
        EventEnvelope envelope,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        CancellationToken ct);

    Task<bool> TryHandleResponseAsync(
        EventEnvelope envelope,
        string defaultPublisherId,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        CancellationToken ct);

    Task<bool> TryHandleChildRunCompletionAsync(
        WorkflowCompletedEvent evt,
        string? publisherActorId,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        CancellationToken ct);
}
```

说明：

1. 一个 capability 可以实现多个入口
2. 但 ownership 仍然在同一能力内部
3. 这样 `llm_call` 的 request/timeout/response/completion 不会再分散到多个壳里

#### `IWorkflowRunCapabilityDescriptor`

descriptor 用来声明路由面，不承载业务逻辑。

建议包含：

1. `CapabilityName`
2. `SupportedStepTypes`
3. `SupportedInternalSignalTypes`
4. `SupportedResponseTypes`
5. `SupportedChildCompletionKinds`

必要时允许：

1. `CanMatchCompletion(...)`
2. `CanMatchResponse(...)`

但必须是显式能力描述，不允许回退到列表顺序探测。

### 6.3 上下文拆分

#### `WorkflowRunReadContext`

只暴露：

1. `State`
2. `CompiledWorkflow`
3. `ActorId`
4. `RunId`

#### `WorkflowRunWriteContext`

只暴露：

1. `PersistStateAsync`
2. `PublishAsync`
3. `SendToAsync`
4. `LogWarningAsync`

#### `WorkflowRunEffectPorts`

只暴露：

1. `EnsureAgentTreeAsync`
2. `ScheduleWorkflowCallbackAsync`
3. `ResolveOrCreateSubWorkflowRunActorAsync`
4. `LinkChildAsync`
5. `CleanupChildWorkflowAsync`
6. `ResolveWorkflowYamlAsync`
7. `CreateWorkflowDefinitionBindEnvelope`
8. `CreateRoleAgentInitializeEnvelope`

这样不同 capability 只拿自己需要的依赖，不再共享万能 context。

## 7. 能力聚合规则

### 7.1 LlmCall 能力

`llm_call` 必须聚合在同一目录：

```text
Capabilities/LlmCall/
├── WorkflowLlmCallCapability.cs
├── WorkflowLlmCallDescriptor.cs
├── WorkflowLlmCallStatePatchContributor.cs
└── WorkflowLlmCallSupport.cs
```

职责：

1. 处理 `llm_call` 的 `StepRequestEvent`
2. 维护 `PendingLlmCalls`
3. 处理 watchdog timeout
4. 处理 `TextMessageEndEvent / ChatResponseEvent`
5. 发布最终 `StepCompletedEvent`

删除：

1. `WorkflowRunLlmRuntime.cs`
2. `WorkflowRunAIResponseRuntime.cs` 中对 `llm_call` 的 ownership

### 7.2 Evaluate 能力

`evaluate` 目录应自持：

1. step request
2. pending evaluation
3. response correlation
4. completion publish

不得依赖 `AIResponseRuntime` 二次转发。

### 7.3 Reflect 能力

`reflect` 与 `evaluate` 一样，必须成为独立 capability，不再通过共享 `AIResponseRuntime` 扇入。

### 7.4 HumanInteraction 能力

`human_input / human_approval` 合理聚合为一个 capability 目录：

1. `pending_human_gates`
2. `resume` token lookup
3. suspend event publish
4. approve / reject 对账

### 7.5 ControlFlow 能力

`delay / wait_signal / race / while` 应聚合在一个 control-flow capability 内。

原因：

1. 都依赖 callback/timeout/signal
2. 都属于流程控制，不是 AI 或 fanout 能力

### 7.6 FanOut 能力

`parallel / foreach / map_reduce` 应聚合在一个 fan-out capability 内。

要求：

1. step request
2. 子 step 生成
3. aggregation completion
4. vote / reduce 专属完成逻辑

必须在同一目录内，而不是拆成 `FanOutRuntime + AggregationCompletionRuntime` 两层。

### 7.7 SubWorkflow 能力

`workflow_call` 应统一拥有：

1. 子工作流创建
2. definition bind
3. child link / cleanup
4. child completion 对账

不得再拆成 owner lifecycle + runtime + registry 多处协作。

### 7.8 Cache 能力

`cache` 应统一拥有：

1. cache hit / miss 判定
2. pending waiters
3. completion 回填
4. 结果回放

不得再分散到 progression completion 里补尾。

## 8. 状态补丁模型重构

### 8.1 删除巨型 PatchSupport

删除：

1. `WorkflowRunStatePatchSupport.cs`

替换为：

1. `WorkflowRunStatePatchAssembler.cs`
2. `IWorkflowRunStatePatchContributor.cs`
3. 每个 capability 自己的 `*StatePatchContributor.cs`

### 8.2 PatchContributor 规则

每个 contributor 只负责自己拥有的切片，例如：

1. `LlmCall` 只负责 `PendingLlmCalls`
2. `HumanInteraction` 只负责 `PendingHumanGates`
3. `FanOut` 只负责 `PendingParallelSteps / PendingForeachSteps / PendingMapReduceSteps`
4. `SubWorkflow` 只负责 `PendingSubWorkflows / PendingChildRunIdsByParentRunId`

owner 不得再知道每个切片怎么 build/apply patch。

## 9. Support 清理策略

### 9.1 删除杂物间

删除或拆散：

1. `WorkflowRunSupport.cs`

替换为明确用途的小文件：

1. `WorkflowSemanticGeneration.cs`
2. `WorkflowPendingTokenLookup.cs`
3. `WorkflowTimeoutSupport.cs`
4. `WorkflowScoreParsing.cs`
5. `WorkflowParentStepIdParser.cs`
6. `WorkflowChildActorIdBuilder.cs`

规则：

1. 若某 helper 只被一个 capability 使用，则直接放回该 capability 目录
2. 只有跨多个 capability 且稳定的 helper 才允许进入 `Run/Support`

## 10. DI 与组合模型

### 10.1 目标

DI 必须成为 capability 注册来源，而不是 owner/suite 手工 `new`。

### 10.2 注册方式

`ServiceCollectionExtensions.cs` 需要扩展：

1. `AddWorkflowRunCapability<TCapability>()`
2. `AddWorkflowRunStatePatchContributor<TContributor>()`

`AddAevatarWorkflow()` 需要注册内建 capabilities：

1. `LlmCall`
2. `Evaluate`
3. `Reflect`
4. `HumanInteraction`
5. `ControlFlow`
6. `FanOut`
7. `SubWorkflow`
8. `Cache`

### 10.3 Owner 构造器目标形态

```csharp
public WorkflowRunGAgent(
    IActorRuntime runtime,
    IRoleAgentTypeResolver roleAgentTypeResolver,
    IEnumerable<IWorkflowPrimitivePack> primitivePacks,
    IEnumerable<IWorkflowRunCapability> capabilities,
    IEnumerable<IWorkflowRunStatePatchContributor> patchContributors,
    IWorkflowDefinitionResolver? workflowDefinitionResolver = null)
```

owner 只做：

1. 构建 `ReadContext / WriteContext / EffectPorts`
2. 构建 `WorkflowRunCapabilityRegistry`
3. 构建 `WorkflowRunStatePatchAssembler`

owner 不得 `new` 任何具体 capability。

## 11. 文件映射

### 11.1 删除

实施后应删除：

1. `WorkflowRunRuntimeSuite.cs`
2. `WorkflowRunRuntimeContext.cs`
3. `WorkflowRunSupport.cs`
4. `WorkflowRunStatePatchSupport.cs`
5. `WorkflowRunAIResponseRuntime.cs`
6. `WorkflowStatefulCompletionHandlerRegistry.cs`
7. `WorkflowInternalSignalRegistry.cs`
8. `WorkflowResponseHandlerRegistry.cs`
9. `WorkflowChildRunCompletionRegistry.cs`
10. `WorkflowStepFamilyDispatchTable.cs`

### 11.2 重写

应重写：

1. `WorkflowPrimitiveExecutionPlanner.cs`
2. `WorkflowAsyncOperationReconciler.cs`
3. `WorkflowRunGAgent.cs`
4. `ServiceCollectionExtensions.cs`

### 11.3 搬迁

| 当前文件 | 目标归属 |
|---|---|
| `WorkflowRunLlmRuntime.cs` | `Capabilities/LlmCall/` |
| `WorkflowRunEvaluationRuntime.cs` | `Capabilities/Evaluate/` |
| `WorkflowRunReflectRuntime.cs` | `Capabilities/Reflect/` |
| `WorkflowRunHumanInteractionRuntime.cs` | `Capabilities/HumanInteraction/` |
| `WorkflowRunControlFlowRuntime.cs` | `Capabilities/ControlFlow/` |
| `WorkflowRunFanOutRuntime.cs` | `Capabilities/FanOut/` |
| `WorkflowRunAggregationCompletionRuntime.cs` | `Capabilities/FanOut/` |
| `WorkflowRunSubWorkflowRuntime.cs` | `Capabilities/SubWorkflow/` |
| `WorkflowRunCacheRuntime.cs` | `Capabilities/Cache/` |
| `WorkflowRunTimeoutCallbackRuntime.cs` | 按能力下沉到对应 capability |
| `WorkflowRunAsyncPolicyRuntime.cs` | `Run/Routing/FailurePolicy/` 或按能力并入 |
| `WorkflowRunDispatchRuntime.cs` | `Run/Routing/WorkflowRunStepRouter.cs` |
| `WorkflowRunStepRequestFactory.cs` | `Run/Routing/StepRequest/` |

## 12. 实施工作包

### WP1. 建立 capability 架构骨架

产物：

1. `IWorkflowRunCapability.cs`
2. `IWorkflowRunCapabilityDescriptor.cs`
3. `WorkflowRunCapabilityRegistry.cs`
4. `WorkflowRunStepRouter.cs`
5. `WorkflowRunCompletionRouter.cs`
6. `WorkflowRunSignalRouter.cs`
7. `WorkflowRunResponseRouter.cs`
8. `WorkflowRunChildCompletionRouter.cs`
9. `WorkflowRunReadContext.cs`
10. `WorkflowRunWriteContext.cs`
11. `WorkflowRunEffectPorts.cs`

DoD：

1. owner 不再依赖 `RuntimeSuite`
2. planner / reconciler 改为 router

### WP2. 迁移 AI 能力

产物：

1. `Capabilities/LlmCall/*`
2. `Capabilities/Evaluate/*`
3. `Capabilities/Reflect/*`

DoD：

1. 删除 `WorkflowRunAIResponseRuntime.cs`
2. `llm/evaluate/reflect` 各自拥有 request + response + timeout/correlation

### WP3. 迁移 Human / ControlFlow

产物：

1. `Capabilities/HumanInteraction/*`
2. `Capabilities/ControlFlow/*`

DoD：

1. `resume/signal/timeout` 对账规则在能力目录内闭环

### WP4. 迁移 FanOut

产物：

1. `Capabilities/FanOut/*`

DoD：

1. `parallel / foreach / map_reduce` 与 aggregation completion 聚合到同一能力目录
2. 删除 `WorkflowRunAggregationCompletionRuntime.cs`

### WP5. 迁移 SubWorkflow / Cache

产物：

1. `Capabilities/SubWorkflow/*`
2. `Capabilities/Cache/*`

DoD：

1. 删除 `WorkflowRunSubWorkflowRuntime.cs`
2. 删除 `WorkflowRunCacheRuntime.cs`
3. 对应逻辑迁入能力目录

### WP6. 重构 patch 体系

产物：

1. `WorkflowRunStatePatchAssembler.cs`
2. `IWorkflowRunStatePatchContributor.cs`
3. capability-local contributors

DoD：

1. 删除 `WorkflowRunStatePatchSupport.cs`
2. owner 不再知道各切片 patch 细节

### WP7. 清理 support / global using / 杂项

产物：

1. `Run/Support/*`
2. 删除 `GlobalUsings.cs` 中非必要项

DoD：

1. `WorkflowRunSupport.cs` 删除
2. AI / Connector / Tool 依赖不再作为全局 using 扩散

### WP8. 文档与门禁

产物：

1. 更新 `docs/WORKFLOW.md`
2. 更新 `Aevatar.Workflow.Core/README.md`
3. 新增架构门禁

DoD：

1. 活跃文档不再出现 `RuntimeSuite / 宽 Context / 多 Registry` 作为推荐架构

## 13. 测试与门禁

### 13.1 单元测试

必须新增：

1. capability router 路由测试
2. capability descriptor 重复注册测试
3. capability-local response correlation 测试
4. capability-local timeout / callback 对账测试
5. patch contributor assemble/apply 测试

### 13.2 集成测试

必须保持通过：

1. `Aevatar.Workflow.Core.Tests`
2. `Aevatar.Workflow.Host.Api.Tests`
3. `Aevatar.Integration.Tests`

### 13.3 新门禁

需要新增或扩展门禁规则：

1. 禁止 `WorkflowRunGAgent` 直接 `new Workflow*Capability`
2. 禁止 `WorkflowRunGAgent` 直接持有 capability 具体字段
3. 禁止 `WorkflowRunSupport.cs` / `WorkflowRunStatePatchSupport.cs` 回流
4. 禁止新增 `RuntimeSuite`、`Registry` 型文件继续承载业务逻辑
5. 禁止 `GlobalUsings.cs` 引入 `Aevatar.AI.*`、`ToolProviders`、`Connectors`

## 14. 完成定义

满足以下条件才算完成：

1. `WorkflowRunGAgent` 不再是 composition root
2. 具体业务能力按 capability 目录聚合
3. response / signal / completion ownership 回到各自能力
4. `WorkflowRunRuntimeSuite`、`WorkflowRunRuntimeContext`、`WorkflowRunSupport`、`WorkflowRunStatePatchSupport` 已删除
5. 新增 stateful capability 不需要修改 owner、suite、support、patch god file
6. `build/test/architecture guards` 全部通过
7. `docs/WORKFLOW.md` 与 `README.md` 与代码结构一致

## 15. 最终判断

当前 `Workflow.Core` 最大的问题不是“类多”，而是：

1. 技术壳过多
2. 能力边界过弱
3. 共享对象过宽
4. 新抽象没有真正兑现开闭原则

因此这轮重构的终局目标必须明确为：

`删除技术切片式拆分，改为 capability-oriented 聚合。`
