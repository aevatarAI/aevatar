# Aevatar.Workflow.Infrastructure

工作流基础设施层。负责 IO、配置装配、Host 能力接线，以及 `IWorkflowRunActorPort` 的运行时适配。

## 当前职责

- workflow 文件系统加载
- workflow capability endpoint 装配
- run/report 工件落盘
- `IWorkflowRunActorPort` 运行时实现

## WorkflowRunActorPort

当前基础设施端口已经按 definition/run split 落地：

- `CreateDefinitionAsync()` 创建 `WorkflowGAgent`
- `DescribeAsync()` 读取 source actor binding，输出 `WorkflowActorBinding`
- `CreateRunAsync(WorkflowDefinitionBinding)` 创建 `WorkflowRunGAgent`，并返回本次创建的 actor 集合用于失败回滚
- `IsWorkflowDefinitionActorAsync()` / `IsWorkflowRunActorAsync()` 区分 actor 类型

`CreateRunAsync()` 会把 definition snapshot 绑定到新的 run actor，而不是复用 source actor 直接执行；若 definition actor 由这次调用新建，也会一并纳入 rollback 元数据。

## API 语义

- `/api/chat`
  - 创建并驱动新的 run actor
- `/api/workflows/resume`
  - 仅接受 run actor id
- `/api/workflows/signal`
  - 仅接受 run actor id

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
