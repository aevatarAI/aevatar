# Aevatar.Workflow.Sagas

Workflow 子系统的 Saga 定义与查询服务。

- `WorkflowExecutionSaga`：按 `correlation_id` 追踪工作流执行生命周期。
- `WorkflowExecutionSagaState`：执行状态、步骤计数、完成结果。
- `IWorkflowExecutionSagaQueryService`：读侧查询接口。

该项目仅包含 Workflow 业务 Saga，不包含通用 Saga 运行时实现。
