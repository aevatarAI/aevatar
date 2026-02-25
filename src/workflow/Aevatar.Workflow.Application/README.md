# Aevatar.Workflow.Application

`Aevatar.Workflow.Application` 承载 Workflow 用例编排（run/query），不做协议适配与基础设施细节。

## 核心服务

- `WorkflowChatRunApplicationService`
  - `ExecuteAsync` 单入口：参数校验 + 获取 run context + 委托执行引擎。
- `WorkflowRunContextFactory`
  - 负责 actor 解析、command context 构造、projection lease 初始化与 live sink attach。
- `WorkflowRunExecutionEngine`
  - 负责请求执行、输出泵送、终态收敛与最终资源回收触发。
- `WorkflowRunCompletionPolicy`
  - 负责输出帧终态判定（`RUN_FINISHED` / `RUN_ERROR`）。
- `WorkflowRunResourceFinalizer`
  - 负责 `detach/release/complete/dispose` 兜底清理。
- `WorkflowRunActorResolver`
  - 无 `actorId` 时创建并绑定 workflow actor。
  - 有 `actorId` 时仅复用既有 actor，不负责切换 workflow。
- `WorkflowRunRequestExecutor`
  - 投递请求事件并处理异常补偿。
- `WorkflowRunOutputStreamer`
  - 读取 run 事件并映射 `WorkflowOutputFrame`。
- `WorkflowExecutionQueryApplicationService`
  - `agents/workflows/runs` 查询门面（经 `IWorkflowExecutionProjectionQueryPort` 读取读侧模型）。
  - `ListAgentsAsync` 仅返回 `WorkflowGAgent`，不扫描暴露非 Workflow actor。
- `WorkflowDefinitionRegistry`
  - 维护 workflow 名称到 YAML 的内存注册表。

## 分层约束

- 本层不依赖 Presentation 协议实现（AGUI/SSE/WS）。
- 本层不包含 `Directory/File` 文件系统扫描逻辑。
- 报告落盘通过 `IWorkflowExecutionReportArtifactSink` 端口交给 Infrastructure。
- 运行约束：一个 workflow 对应一个 actor，workflow 与 actor 绑定后不可变。

## DI 入口

- `AddWorkflowApplication()`
  - 注册应用层用例与默认 `NoopWorkflowExecutionReportArtifactSink`。

宿主应组合：`Application + Projection + Infrastructure`，而不是在 API 中实现业务编排。
