# 架构审查评分报告（全项目）

审查时间：2026-02-15  
审查范围：`src/`、`workflow/` 子系统、`Host`、`CQRS Projection`、`Infrastructure`、测试与文档一致性。

## 一、总评分

**综合得分：73 / 100（评级：B）**

评分维度（10 分制）：

| 维度 | 权重 | 得分 | 加权分 |
|---|---:|---:|---:|
| 分层清晰度（Host/Application/Core/Infrastructure） | 20% | 8.0 | 16.0 |
| 依赖反转与边界纯度 | 20% | 7.0 | 14.0 |
| 开闭原则与扩展能力 | 15% | 8.0 | 12.0 |
| 运行时可靠性（流/背压/完成性） | 20% | 6.0 | 12.0 |
| 可测试性与质量门禁 | 10% | 8.0 | 8.0 |
| 运维可用性（配置/可观测/落盘） | 10% | 7.0 | 7.0 |
| 文档与命名一致性 | 5% | 8.0 | 4.0 |

结论：架构主干已经成型，分层与扩展性明显优于早期版本；但在**流式可靠性**与**边界彻底解耦**上仍有关键改进项。

## 二、主要问题（按严重级别）

### P0（必须优先修复）

1. **流式背压下存在“终止事件丢失 -> 请求卡住”风险**  
证据链：
- 默认通道为 `DropWrite`：`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowRunEventContracts.cs:143`
- 写失败直接异常：`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowRunEventContracts.cs:169`
- AGUI projector 捕获后直接 `DetachRunEventSink`：`src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/WorkflowExecutionAGUIEventProjector.cs:47`
- 输出侧依赖终止事件退出：`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunOutputStreamer.cs:18`

风险：高并发 token 流时可能丢失 `RUN_FINISHED/RUN_ERROR`，导致 SSE/WS 请求悬挂。

### P1（高优先级）

1. **Application 对 Projection 实现耦合仍偏重**  
- `Application` 直接引用 `Workflow.Projection`：`src/workflow/Aevatar.Workflow.Application/Aevatar.Workflow.Application.csproj:11`
- 编排接口直接暴露 Projection 类型：`src/workflow/Aevatar.Workflow.Application/Orchestration/IWorkflowExecutionRunOrchestrator.cs:3`

2. **Infrastructure 通过 Projection Service 读取功能开关，边界下沉不彻底**  
- `FileSystemWorkflowExecutionReportArtifactSink` 依赖 `IWorkflowExecutionProjectionService`：`src/workflow/Aevatar.Workflow.Infrastructure/Reporting/FileSystemWorkflowExecutionReportArtifactSink.cs:12`

3. **存在 Service Locator 用法**  
- `ConnectorCallModule` 通过 `ctx.Services.GetService(...)` 取依赖：`src/workflow/Aevatar.Workflow.Core/Modules/ConnectorCallModule.cs:37`

### P2（中优先级）

1. **ReadModel 默认内存存储无保留策略、无持久化**  
- 默认注册内存存储：`src/workflow/Aevatar.Workflow.Projection/DependencyInjection/ServiceCollectionExtensions.cs:34`
- 内存字典长期累积：`src/workflow/Aevatar.Workflow.Projection/Stores/InMemoryWorkflowExecutionReadModelStore.cs:12`

2. **Run 事件通道由应用层直接 `new`，替换性不足**  
- `new WorkflowRunEventChannel()`：`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowChatRunApplicationService.cs:133`

## 三、架构优点

1. Host 职责基本收敛到协议与组合：`src/Aevatar.Host.Api/Endpoints/ChatEndpoints.cs:17`  
2. AGUI 映射已采用 handler 注册表，新增映射可“新增类 + 注册”：`src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/DependencyInjection/ServiceCollectionExtensions.cs:12`  
3. Workflow 模块依赖推断/配置已策略化：`src/workflow/Aevatar.Workflow.Core/Composition/IWorkflowModuleDependencyExpander.cs:5`  
4. 分层文档已较完整，且全量构建/测试通过（当前测试通过总数 183）。

## 四、整改建议（按顺序）

1. **先修 P0**：将 run event sink 改为可背压等待（或终止事件高优先级保底），并建立“终止事件必达”契约测试。  
2. **再修 P1**：抽 `IWorkflowProjectionPort` 到 Application.Abstractions，Application 不再直接依赖 Projection 实现。  
3. **修 P1（边界）**：报告开关改为 `IOptions<WorkflowExecutionReportArtifactOptions>`，Infrastructure 不再读 Projection Service。  
4. **修 P1（DI）**：Connector registry 改构造注入，移除 Service Locator。  
5. **修 P2**：提供可替换持久化 read model store（文件系统/Redis/Postgres 至少一种）及 TTL/清理策略。

## 五、复评门槛

达到以下条件可将评分提升到 85+：

1. 消除 P0 风险并补齐回归测试。  
2. Application 与 Projection 实现彻底解耦（仅依赖抽象端口）。  
3. Infrastructure 不再跨边界读取 Projection 运行时对象。  
4. ReadModel 引入持久化与清理策略。
