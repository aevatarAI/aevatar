# 解决方案命名审计（最终定稿）

## 1. 最终结论

1. `GAgent` 术语保留，不替换为 `Agent`。
2. 缩写统一全大写：`AI`、`LLM`、`MCP`、`AGUI`、`CQRS`。
3. CQRS 投影命名按读模型语义，不按入口协议命名。
4. `Chat*` 仅用于对话入口/协议层；执行域读模型统一使用 `WorkflowExecution*`。
5. 不做兼容别名，不保留新旧命名并存。

## 2. 强制命名规范

1. 项目名、目录名、`RootNamespace`、主命名空间一致。
2. 抽象命名优先业务语义，不使用历史实现词汇。
3. 接口命名体现职责边界：执行编排、投影、存储、查询分离。
4. 集合/容器项目与命名空间使用复数（如 `Providers`、`Projections`、`Workflows`）。

## 3. CQRS 与 Projection 命名边界（最终）

- 协议层（可保留 `Chat`）：
  - `ChatRequestEvent`
  - `ChatResponseEvent`
- 执行域读模型层（统一 `WorkflowExecution`）：
  - ReadModel、Projector、Reducer、Coordinator、Store、Service、Session、Context、Report

说明：`ChatRequestEvent/ChatResponseEvent` 是输入输出协议事件；`WorkflowExecution*` 是查询侧读模型语义，二者不混用。

## 4. 统一重命名映射（必须落地）

| 当前命名 | 目标命名 |
|---|---|
| `ChatRun*` | `WorkflowExecution*` |
| `WorkflowExecutionReport` | `WorkflowExecutionReport` |
| `WorkflowExecutionSummary` | `WorkflowExecutionSummary` |
| `WorkflowExecutionStepTrace` | `WorkflowExecutionStepTrace` |
| `WorkflowExecutionRoleReply` | `WorkflowExecutionRoleReply` |
| `WorkflowExecutionTimelineEvent` | `WorkflowExecutionTimelineEvent` |
| `WorkflowExecutionTopologyEdge` | `WorkflowExecutionTopologyEdge` |
| `IWorkflowExecutionProjectionService` | `IWorkflowExecutionProjectionService` |
| `WorkflowExecutionProjectionService` | `WorkflowExecutionProjectionService` |
| `IWorkflowExecutionProjectionCoordinator` | `IWorkflowExecutionProjectionCoordinator` |
| `WorkflowExecutionProjectionCoordinator` | `WorkflowExecutionProjectionCoordinator` |
| `IWorkflowExecutionProjector` | `IWorkflowExecutionProjector` |
| `WorkflowExecutionReadModelProjector` | `WorkflowExecutionReadModelProjector` |
| `IWorkflowExecutionEventReducer` | `IWorkflowExecutionEventReducer` |
| `WorkflowExecutionEventReducerBase` | `WorkflowExecutionEventReducerBase` |
| `WorkflowExecutionProjectionContext` | `WorkflowExecutionProjectionContext` |
| `WorkflowExecutionProjectionSession` | `WorkflowExecutionProjectionSession` |
| `IWorkflowExecutionReadModelStore` | `IWorkflowExecutionReadModelStore` |
| `InMemoryWorkflowExecutionReadModelStore` | `InMemoryWorkflowExecutionReadModelStore` |
| `WorkflowExecutionProjectionOptions` | `WorkflowExecutionProjectionOptions` |
| `WorkflowExecutionProjectionSubscriptionRegistry` | `WorkflowExecutionProjectionSubscriptionRegistry` |
| `IWorkflowExecutionProjectionSubscriptionRegistry` | `IWorkflowExecutionProjectionSubscriptionRegistry` |
| `WorkflowExecutionReportWriter` | `WorkflowExecutionReportWriter` |

## 5. 其他关键命名修正

| 当前命名 | 目标命名 |
|---|---|
| `AgUi*` | `AGUI*` |
| `LLM*` | `LLM*` |
| `MCP*` | `MCP*` |
| `Aevatar.CQRS.Projections` | `Aevatar.CQRS.Projections` |
| `Aevatar.CQRS.Projections.Abstractions` | `Aevatar.CQRS.Projections.Abstractions` |
| `WorkflowModuleFactory` | `WorkflowModuleFactory` |
| `IGAgentExecutionHook` | `IGAgentExecutionHook` |
| `GAgentExecutionHookContext` | `GAgentExecutionHookContext` |
| `IAIGAgentExecutionHook`（AI层） | `IAIGAgentExecutionHook` |
| `AIGAgentExecutionHookContext` | `AIGAgentExecutionHookContext` |

## 6. 解决方案组织规范（最终）

1. API 仅作为 Host/Adapter，不承载 CQRS 核心实现。
2. CQRS 投影核心放在独立项目（`Aevatar.CQRS.Projections*`）。
3. 依赖方向：`Hosts -> CQRS.Abstractions -> CQRS.Core -> Foundation`。
4. `Reporting` 只依赖读模型抽象，不反向依赖 Endpoint。

## 7. 执行顺序

1. 先统一执行域术语：`ChatRun* -> WorkflowExecution*`。
2. 再统一 CQRS 项目名/命名空间缩写：`CQRS -> CQRS`。
3. 再统一类型缩写风格：`AgUi/LLM/MCP -> AGUI/LLM/MCP`。
4. 再清理 Hook 和 Workflow 历史命名残留。
5. 最后统一文档与示例代码引用。

## 8. 非目标

- 不保留旧命名兼容层。
- 不做 namespace 转发。
- 不接受同一概念多个名字并存。
