# Aevatar.Workflow.Infrastructure

工作流基础设施层。负责 IO、配置装配、Host 能力接线，以及 workflow actor binding 查询和 `IWorkflowRunActorPort` 的运行时适配。

## 当前职责

- workflow 文件系统加载
- workflow capability endpoint 装配
- run/report 工件落盘
- `IWorkflowRunActorPort` 运行时实现

## WorkflowRunActorPort

当前基础设施端口已经按 definition/run split 落地：

- `CreateDefinitionAsync()` 创建 `WorkflowGAgent`
- `CreateRunAsync(WorkflowDefinitionBinding)` 创建 `WorkflowRunGAgent`，并返回本次创建的 actor 集合用于失败回滚
- `BindWorkflowDefinitionAsync()` 负责向 definition actor 发送权威绑定事件
- `ParseWorkflowYamlAsync()` 负责入口 YAML 校验

`CreateRunAsync()` 会把 definition snapshot 绑定到新的 run actor，而不是复用 source actor 直接执行；若 definition actor 由这次调用新建，也会一并纳入 rollback 元数据。

当前实现约束：

- source actor binding 读取统一通过 `IWorkflowActorBindingReader`。其基础设施实现 `RuntimeWorkflowActorBindingReader` 使用 workflow actor 自身的 query/reply 协议读取 binding，而不是读取 generic raw state payload；因此 Local / Orleans 共用同一套语义。
- 对显式传入的 `DefinitionActorId`，端口采用强约束复用策略：
  - actor 不存在：按该 id 创建新的 `WorkflowGAgent`
  - actor 已存在且是 `WorkflowGAgent`：若 definition 已一致则直接复用，不一致则原地重绑
  - actor 已存在但不是 `WorkflowGAgent`：立即失败
- 只有 `DefinitionActorId` 为空时，端口才允许创建无固定 id 的 definition actor；这一路径只用于 inline/ad-hoc workflow，不用于 registry-backed named workflow。

## API 语义

- `/api/chat`
  - 创建并驱动新的 run actor
- `/api/workflows/resume`
  - 通过 `IWorkflowActorBindingReader` 校验目标必须是 run actor
- `/api/workflows/signal`
  - 通过 `IWorkflowActorBindingReader` 校验目标必须是 run actor

## 目录

```
Aevatar.Workflow.Infrastructure/
├── CapabilityApi/
│   ├── ChatEndpoints.cs
│   ├── ChatQueryEndpoints.cs
│   └── ChatRunRequestNormalizer.cs
├── Runs/
│   └── WorkflowRunActorPort.cs
├── Workflows/
│   ├── WorkflowDefinitionFileLoader.cs
│   └── WorkflowDefinitionBootstrapHostedService.cs
└── Reporting/
```

## 分层边界

- 依赖 Application abstractions
- 可以依赖 Workflow.Core 作为 runtime adapter 的落地类型来源
- 不在本层编排 run 业务逻辑
- 不在本层维护 workflow 事实状态
