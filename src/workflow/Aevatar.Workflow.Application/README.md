# Aevatar.Workflow.Application

Workflow 应用层实现项目，承载 chat run 用例编排。

## 职责

- `WorkflowChatRunApplicationService`
  - `ExecuteAsync` 单入口编排：解析/创建 Actor、触发运行、流式输出、finalize/rollback 收敛。
- `WorkflowExecutionQueryApplicationService`
  - 提供 `agents/workflows/runs` 查询，屏蔽 Host 对 projection/runtime 的直接依赖。
- `WorkflowExecutionRunOrchestrator`
  - 统一 start/wait/complete/rollback 编排。
- `ActorRuntimeWorkflowExecutionTopologyResolver`
  - 基于 runtime 快照解析拓扑。
- `WorkflowDefinitionRegistry`
  - 维护 workflow 名称到 YAML 的映射。
- `IWorkflowChatRequestEnvelopeFactory`
  - 统一构造 `ChatRequestEvent` 的 envelope 与 metadata，避免协议硬编码散落。
- `IWorkflowExecutionReportArtifactSink`
  - 报告落盘基础设施抽象（默认 `FileSystemWorkflowExecutionReportArtifactSink`）。

`Aevatar.Host.Api` 只依赖本项目的抽象接口进行调用。
