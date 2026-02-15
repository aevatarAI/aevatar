# Aevatar.Workflow.Application.Abstractions

Workflow 应用层抽象：定义 `Host` 与 `workflow` 应用实现之间的稳定契约。

## 包含内容

- `IWorkflowChatRunApplicationService`：chat run 用例入口。
- `IWorkflowExecutionRunOrchestrator`：run 生命周期编排抽象。
- `IWorkflowExecutionTopologyResolver`：拓扑解析策略抽象。
- `IWorkflowDefinitionRegistry`：工作流定义注册与查询抽象。

该项目不包含具体实现。
