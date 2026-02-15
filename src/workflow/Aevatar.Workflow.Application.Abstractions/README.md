# Aevatar.Workflow.Application.Abstractions

Workflow 应用层抽象：定义 `Host` 与 `workflow` 应用实现之间的稳定契约。

## 包含内容

- `IWorkflowChatRunApplicationService`：chat run 用例契约（`ExecuteAsync` 单入口）。
- `IWorkflowExecutionQueryApplicationService`：读侧查询契约（agents/workflows/runs）。
- `WorkflowChatRun*` / `WorkflowRun*` 模型：输出帧、执行结果、查询 DTO。
- `IWorkflowDefinitionRegistry`：工作流定义注册与查询抽象。

该项目不包含具体实现。
