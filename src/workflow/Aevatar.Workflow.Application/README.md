# Aevatar.Workflow.Application

工作流应用层。负责 workflow realtime interaction owner、命令目标解析、attach-only projection lease 绑定、live sink 挂接、accepted receipt 生成、输出泵送与查询门面，不直接持有 workflow 业务事实。

## 关键职责

- 解析请求来源：registry / inline bundle / source actor
- 生成 interaction 用 `WorkflowRunCommandTarget` 与 accepted-only 用 `WorkflowRunAcceptedCommandTarget`
- 通过 `IWorkflowRunActorPort` 创建 definition actor 或 run actor
- 为 run actor 建立 projection lifecycle 和 live sink
- 生成 `WorkflowChatRunAcceptedReceipt`
- 通过 `IWorkflowChatRunInteractionPort` 启动实时交互，并经 CQRS Core 通用 event stream 持续输出 `WorkflowRunEventEnvelope`
- 通过 `ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>` 启动 fork run，seed 只走 execution request/start 链路
- 暴露读侧查询门面

## Run 主链路

### WorkflowRunCommandTargetResolver

把所有输入统一折叠成可执行 target：

- `workflowYamls` 优先于 `workflow`
- `workflow` 走 `IWorkflowDefinitionCatalog`，并解析出规范 definition actor id `workflow-definition:{workflow_name_lower}`
- `actorId` 作为 definition source lookup
- source actor 会先经 `IWorkflowActorBindingReader.GetAsync()` 解析成 `WorkflowActorBinding`
- 若 source actor 是 run actor 且缺失 `DefinitionActorId`，resolver 会回落到 registry 中该 workflow 的规范 definition actor id
- 真正执行永远落到新的 `WorkflowRunGAgent`

额外约束：

- registry-backed workflow 必须复用稳定 definition actor id，避免默认 workflow-name 启动路径不断堆积不可达 definition actor。
- inline workflow bundle 不注册固定 definition actor id；其 definition 只对当前 run 创建过程负责。
- resolver 只向 infrastructure 传递“权威 definition actor id”或空值，不再传递语义不明的占位空 id。

### WorkflowRunObservationLifecycle

- 调用 resolver 拿到 run actor
- 若 resolver 本次新建了 actor，而 observation lifecycle 启动失败，负责回滚这些新建 actor
- 创建 `EventChannel<WorkflowRunEvent>`
- 通过 projection lifecycle port 建立 run-isolated projection lease
- 产出供 interaction service 判定是否继续 dispatch 的 `CommandObservationBindingResult`

### Realtime Interaction / Detached Dispatch

- `IWorkflowChatRunInteractionPort` 走完整实时交互路径：生成 command/correlation id、激活本次 run actor observation scope、驱动内部默认 CQRS interaction service、接收 accepted receipt、消费 sink 并持续输出 `WorkflowRunEventEnvelope`
- 默认 CQRS interaction service 只作为 `IWorkflowChatRunInteractionPort` 内部 non-fallback plumbing，不作为 public/resolvable realtime 入口
- `DefaultCommandDispatchService<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>` 走 accepted-only 路径：复用 CQRS command skeleton，只返回 accepted receipt；该路径不建立 live sink，也不 drain session event stream
- realtime fallback 由 `IWorkflowChatRunInteractionPort` 拥有；accepted-only dispatch 仍保持独立 accepted-only 语义
- 真正的 envelope 投递由 CQRS Core 的 `ActorCommandTargetDispatcher` 通过 `IActorDispatchPort` 完成，`IActorRuntime` 继续负责目标 actor 的获取/创建与拓扑
- 状态快照由 `WorkflowRunFinalizeEmitter` 统一在收尾阶段补发
- `resume/signal` 入口也收敛为标准 CQRS 命令：Host 只依赖 `ICommandDispatchService<WorkflowResumeCommand/...>` 与 `ICommandDispatchService<WorkflowSignalCommand/...>`
- `fork` 入口同样收敛为标准 CQRS 命令：Host 与自动 fork coordinator 只构造 `WorkflowForkRunCommand`，由 target resolver 查询 seed、创建新 run，并由 fork pipeline 从 target 的 prepared request 把 `WorkflowChatRequestEvent.ForkSeed` 投递给新 run actor

## Query 语义

`WorkflowExecutionQueryApplicationService` 当前查询的是 run actor 快照：

- `ListAgentsAsync()` 返回 `WorkflowRunGAgent[...]`
- `GetActorSnapshotAsync()` 的 `actorId` 是 run actor id
- timeline / graph 也以 run actor 为根节点

## 当前边界

- 不直接依赖 Infrastructure 实现
- 不直接操作 `WorkflowRunState`
- 不维护 `actorId -> context` 进程内事实映射
- 所有读写都经 abstraction ports
- 不从 Application 层猜测 definition actor 的匿名生命周期；registry 定义与 source actor binding 是唯一入口

## 主要目录

```
Aevatar.Workflow.Application/
├── RunForks/
│   ├── WorkflowForkRunAcceptedReceiptFactory.cs
│   ├── WorkflowForkRunCommandDispatchService.cs
│   ├── WorkflowForkRunCommandEnvelopeFactory.cs
│   ├── WorkflowForkRunCommandTarget.cs
│   ├── WorkflowForkRunCommandTargetResolver.cs
│   └── WorkflowRunForkCoordinator.cs
├── Runs/
│   ├── WorkflowRunAcceptedReceiptFactory.cs
│   ├── WorkflowRunAcceptedCommandTarget.cs
│   ├── WorkflowRunAcceptedCommandTargetResolver.cs
│   ├── WorkflowRunActorResolver.cs
│   ├── WorkflowRunControlAcceptedReceiptFactory.cs
│   ├── WorkflowRunControlCommandTarget.cs
│   ├── WorkflowRunControlCommandTargetResolverBase.cs
│   ├── WorkflowRunCommandTarget.cs
│   ├── WorkflowRunObservationLifecycle.cs
│   ├── WorkflowRunCommandTargetResolver.cs
│   ├── WorkflowResumeCommandEnvelopeFactory.cs
│   ├── WorkflowResumeCommandTargetResolver.cs
│   ├── WorkflowSignalCommandEnvelopeFactory.cs
│   ├── WorkflowSignalCommandTargetResolver.cs
│   └── WorkflowRunFinalizeEmitter.cs
├── Queries/
│   └── WorkflowExecutionQueryApplicationService.cs
├── Workflows/
│   └── WorkflowDefinitionCatalog.cs
└── Reporting/
```
