# Aevatar.Workflow.Application

`Aevatar.Workflow.Application` 承载 Workflow 用例编排（run/query），不做协议适配与基础设施细节。

## 核心服务

- `WorkflowChatRunApplicationService`
  - `ExecuteAsync` 单入口：start -> execute -> stream -> finalize/rollback。
- `WorkflowExecutionRunOrchestrator`
  - 投影生命周期编排（start/wait/complete/rollback）。
- `WorkflowRunActorResolver`
  - 解析/创建 workflow actor。
- `WorkflowRunRequestExecutor`
  - 投递请求事件并处理异常补偿。
- `WorkflowRunOutputStreamer`
  - 读取 run 事件并映射 `WorkflowOutputFrame`。
- `WorkflowExecutionQueryApplicationService`
  - `agents/workflows/runs` 查询门面。
- `WorkflowExecutionReportMapper`
  - 将 Projection read model 映射到应用层 `WorkflowRunReport/Summary`。
- `WorkflowDefinitionRegistry`
  - 维护 workflow 名称到 YAML 的内存注册表。

## 分层约束

- 本层不依赖 Presentation 协议实现（AGUI/SSE/WS）。
- 本层不包含 `Directory/File` 文件系统扫描逻辑。
- 报告落盘通过 `IWorkflowExecutionReportArtifactSink` 端口交给 Infrastructure。

## DI 入口

- `AddWorkflowApplication()`
  - 注册应用层用例与默认 `NoopWorkflowExecutionReportArtifactSink`。

宿主应组合：`Application + Projection + Infrastructure`，而不是在 API 中实现业务编排。
