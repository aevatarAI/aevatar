# Aevatar.Workflow.Application

Workflow 应用层实现项目，承载 chat run 用例编排。

## 职责

- `WorkflowChatRunApplicationService`
  - 解析/创建 Actor，构建 `ChatRequestEvent`，触发运行。
  - 监听 AGUI 事件直到终止。
  - 收敛 projection 生命周期并执行 best-effort 报告写出。
- `WorkflowExecutionRunOrchestrator`
  - 统一 start/wait/complete/rollback 编排。
- `ActorRuntimeWorkflowExecutionTopologyResolver`
  - 基于 runtime 快照解析拓扑。
- `WorkflowDefinitionRegistry`
  - 维护 workflow 名称到 YAML 的映射。

`Aevatar.Host.Api` 只依赖本项目的抽象接口进行调用。
