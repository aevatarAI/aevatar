# Aevatar.Workflow.Application

Workflow 应用层实现项目，承载 chat run 用例编排。

## 职责

- `WorkflowChatRunApplicationService`
  - `ExecuteAsync` 单入口编排：启动 run、触发执行、流式输出、finalize/rollback 收敛。
- `WorkflowRunActorResolver`
  - 负责 Actor 解析/创建（existing actor 与 workflow yaml 两条路径）。
- `WorkflowRunRequestExecutor`
  - 负责请求事件投递与失败补偿（写入 `RUN_ERROR`）。
- `WorkflowRunOutputStreamer`
  - 负责 run 事件读取与 `WorkflowOutputFrame` 映射，不混入执行编排逻辑。
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
  - 报告工件端口抽象（应用层默认 `Noop`，由 Infrastructure 注入具体实现）。

## 分层约束

- 本项目不再依赖 `Aevatar.Presentation.AGUI` 与 `Aevatar.Workflow.Presentation.AGUIAdapter`。
- 输出通道通过 `IWorkflowRunEventSink`（定义在 `Aevatar.Workflow.Projection`）解耦。
- Host 只调用应用层契约，不直接参与 workflow/projection 细节。

`Aevatar.Host.Api` 只依赖本项目的抽象接口进行调用。
