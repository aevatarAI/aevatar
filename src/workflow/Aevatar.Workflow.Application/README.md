# Aevatar.Workflow.Application

工作流应用层。负责 run 用例编排、projection lease 建立、live sink 挂接、输出泵送与查询门面，不直接持有 workflow 业务事实。

## 关键职责

- 解析请求来源：registry / inline bundle / source actor
- 生成 `WorkflowDefinitionBinding`
- 通过 `IWorkflowRunActorPort` 创建 definition actor 或 run actor
- 为 run actor 建立 projection lifecycle 和 live sink
- 发送 `ChatRequestEvent`
- 把 `WorkflowRunEvent` 映射成 `WorkflowOutputFrame`
- 暴露读侧查询门面

## Run 主链路

### WorkflowRunActorResolver

把所有输入统一折叠成可执行 binding：

- `workflowYamls` 优先于 `workflow`
- `workflow` 走 `IWorkflowDefinitionRegistry`
- `actorId` 作为 definition source lookup
- source actor 会先经 `DescribeAsync()` 解析成 `WorkflowActorBinding`
- 真正执行永远落到新的 `WorkflowRunGAgent`

### WorkflowRunContextFactory

- 调用 resolver 拿到 run actor
- 为 run actor 创建 `CommandContext`
- 创建 `EventChannel<WorkflowRunEvent>`
- 通过 projection lifecycle port 建立 run-isolated projection lease

### WorkflowRunExecutionEngine

- 构造 `ChatRequestEvent` 信封
- 将请求投递到 run actor
- 消费 sink 并持续输出 `WorkflowOutputFrame`
- 完成后补发 state snapshot，并统一清理 lease/sink

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

## 主要目录

```
Aevatar.Workflow.Application/
├── Runs/
│   ├── WorkflowChatRunApplicationService.cs
│   ├── WorkflowRunActorResolver.cs
│   ├── WorkflowRunContextFactory.cs
│   ├── WorkflowRunExecutionEngine.cs
│   ├── WorkflowRunRequestExecutor.cs
│   ├── WorkflowRunOutputStreamer.cs
│   └── WorkflowRunStateSnapshotEmitter.cs
├── Queries/
│   └── WorkflowExecutionQueryApplicationService.cs
├── Workflows/
│   └── WorkflowDefinitionRegistry.cs
└── Reporting/
```
