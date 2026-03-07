# Aevatar.Workflow.Core

`Aevatar.Workflow.Core` 是 workflow 子系统的领域内核。当前实现已经从旧的 `RuntimeSuite/Reconciler` 结构，收口为 capability-oriented run core。

权威蓝图见 [workflow-core-capability-oriented-refactor-blueprint-2026-03-08.md](/Users/auric/aevatar/docs/architecture/workflow-core-capability-oriented-refactor-blueprint-2026-03-08.md)。

## 当前模型

1. `WorkflowGAgent`
   - definition/binding owner
2. `WorkflowRunGAgent`
   - 单次 run owner
   - 唯一 run 事实源
3. `WorkflowPrimitiveExecutorRegistry`
   - 只负责无状态 primitive
4. `Capabilities/*`
   - 负责有状态业务能力

## 目录主干

```text
Aevatar.Workflow.Core/
├── WorkflowGAgent.cs
├── WorkflowRunGAgent.cs
├── WorkflowRunGAgent.Lifecycle.cs
├── WorkflowRunGAgent.ExternalInteractions.cs
├── WorkflowRunGAgent.Infrastructure.cs
├── WorkflowRunEffectDispatcher.cs
├── WorkflowCompilationService.cs
├── workflow_state.proto
├── workflow_run_state.proto
├── Run/
│   ├── Context/
│   ├── Routing/
│   ├── State/
│   └── Support/
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
└── Expressions/
```

## 结构规则

### 1. Owner shell

[WorkflowRunGAgent.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs) 只保留：

1. ingress handlers
2. state transition 入口
3. capability router 调用
4. run finalization 决策

### 2. 共享边界

共享能力只允许通过：

1. [WorkflowRunReadContext.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/Run/Context/WorkflowRunReadContext.cs)
2. [WorkflowRunWriteContext.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/Run/Context/WorkflowRunWriteContext.cs)
3. [WorkflowRunEffectPorts.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/Run/Context/WorkflowRunEffectPorts.cs)

不允许再回到一个万能 `RuntimeContext`。

### 3. 路由

运行时路由统一由 `Run/Routing/` 负责：

1. step
2. completion
3. internal signal
4. response
5. child completion
6. resume
7. external signal

### 4. 状态补丁

`WorkflowRunStatePatchedEvent` 已改成 contributor 模型：

1. [WorkflowRunStatePatchAssembler.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/Run/State/WorkflowRunStatePatchAssembler.cs)
2. [IWorkflowRunStatePatchContributor.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/Run/State/IWorkflowRunStatePatchContributor.cs)

每个 capability 维护自己的 patch contributor。

## 扩展规则

### 无状态扩展

使用：

1. [IWorkflowPrimitiveExecutor.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/IWorkflowPrimitiveExecutor.cs)
2. [IWorkflowPrimitivePack.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/IWorkflowPrimitivePack.cs)
3. [ServiceCollectionExtensions.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/ServiceCollectionExtensions.cs)

### 有状态扩展

如果新能力需要：

1. pending facts
2. timeout / retry / callback
3. external response correlation
4. human gate / signal wait
5. fanout aggregation
6. child workflow lifecycle

就必须扩：

1. `workflow_run_state.proto`
2. 一个 capability 目录
3. 一个 patch contributor
4. owner 的 built-in capability 装配

## 当前内建 stateful capability

1. [WorkflowLlmCallCapability.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/Capabilities/LlmCall/WorkflowLlmCallCapability.cs)
2. [WorkflowEvaluateCapability.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/Capabilities/Evaluate/WorkflowEvaluateCapability.cs)
3. [WorkflowReflectCapability.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/Capabilities/Reflect/WorkflowReflectCapability.cs)
4. [WorkflowHumanInteractionCapability.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/Capabilities/HumanInteraction/WorkflowHumanInteractionCapability.cs)
5. [WorkflowControlFlowCapability.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/Capabilities/ControlFlow/WorkflowControlFlowCapability.cs)
6. [WorkflowFanOutCapability.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/Capabilities/FanOut/WorkflowFanOutCapability.cs)
7. [WorkflowSubWorkflowCapability.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/Capabilities/SubWorkflow/WorkflowSubWorkflowCapability.cs)
8. [WorkflowCacheCapability.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/Capabilities/Cache/WorkflowCacheCapability.cs)

## 不允许回流的旧模式

1. 统一的大型运行时装配壳。
2. 同时暴露读、写、effect 的宽上下文对象。
3. 承载多能力逻辑的通用 `Support`/`Patch` 杂物间。
4. 靠列表顺序或二次 fan-in 驱动的隐式路由。
5. 跨能力代持 response/completion ownership 的通用运行时壳。
