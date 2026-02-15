# 架构审计报告（2026-02-15）

## 1. 审计范围与方法

- 范围：`src/`、`demos/`、`test/`、`docs/`、`aevatar.slnx`
- 静态审计：
  - 扫描全部 `csproj` 项目引用关系（`src` 依赖边共 47 条）
  - 抽样审阅核心实现：Foundation Runtime、Workflow、CQRS Projection、AGUI Adapter、Host 层
- 动态基线：
  - `dotnet build aevatar.slnx --nologo`：通过（0 error，0 warning）
  - `dotnet test aevatar.slnx --nologo`：通过（13 + 77 + 33 + 52）
- 结论：`src` 项目依赖图无循环（acyclic）。

## 2. 架构现状总览

- Foundation 分层清晰：`Aevatar.Foundation.Abstractions -> Core -> Runtime`
- Workflow/AI 解耦基本成立：`Aevatar.Workflow.Core` 仅依赖 `Aevatar.AI.Abstractions`，不依赖 `Aevatar.AI.Core`
- CQRS 已按三层拆分：`Aevatar.CQRS.Projection.Abstractions/Core/WorkflowExecution`
- AGUI 与 Workflow 的耦合点已收敛到适配层：`Aevatar.Workflow.Presentation.AGUIAdapter`
- Host 层已从 `Projection` 目录收敛到编排职责：`Aevatar.Host.Api/Orchestration/WorkflowExecutionRunOrchestrator.cs`
- 运行策略已确认：**同一 Actor 多 Run 不做事件隔离**，客户端/请求端可消费该 Actor 的全量事件流；请求收尾通过当前 `runId` 判断（`src/Aevatar.Host.Api/Endpoints/ChatEndpoints.cs`）。

## 2.1 整改完成状态（本次落地）

| 项 | 状态 | 关键改动 |
|---|---|---|
| D-01（多 Run 不隔离契约） | ✅ 已固化 | 补充 API 文档语义与测试断言：`src/Aevatar.Host.Api/README.md`、`test/Aevatar.Host.Api.Tests/ChatEndpointsInternalTests.cs` |
| A-01（WorkflowLoop 单活动 Run） | ✅ 已修复 | `WorkflowLoopModule` 改为按 `runId` 维护活动集合：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs` |
| A-02（RunStarted 重复与 threadId 不一致） | ✅ 已修复 | API 不再主动发送 `RunStartedEvent`，统一由 `StartWorkflowEvent` 投影；`threadId` 统一取 `PublisherId`：`src/Aevatar.Host.Api/Endpoints/ChatEndpoints.cs`、`src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/EventEnvelopeToAGUIEventMapper.cs` |
| A-03（Endpoint 责任偏重） | ✅ 已收敛 | `ChatEndpoints` 抽取 Actor 解析、请求执行、投影收尾辅助流程，减少协议层内聚合复杂度：`src/Aevatar.Host.Api/Endpoints/ChatEndpoints.cs` |
| A-04（Abstractions 含默认实现 + 字符串反射） | ✅ 已修复 | `IRoleAgentTypeResolver` 仅保留接口；默认实现迁移到 AI.Core 并在组合层显式注册：`src/Aevatar.AI.Abstractions/Agents/IRoleAgentTypeResolver.cs`、`src/Aevatar.AI.Core/Agents/RoleGAgentTypeResolver.cs`、`src/Aevatar.Bootstrap/ServiceCollectionExtensions.cs` |
| A-05（Host.Api 与 Host.Gateway 协议重叠） | ✅ 已修复 | `Host.Gateway` 不再暴露并行 Chat 协议端点，移除 `ChatEndpoints.cs`：`src/Aevatar.Host.Gateway/Program.cs`、`src/Aevatar.Host.Gateway/README.md` |
| A-06（Workflow 目录组织） | ✅ 已完成 | Workflow 相关项目统一落到 `src/workflow/`，并修正 sln/slnx 路径 |
| A-07（无界流/吞异常） | ✅ 已修复 | `InMemoryStream` 与 `AGUIEventChannel` 改为有界通道，支持背压策略与错误日志：`src/Aevatar.Foundation.Runtime/Streaming/*`、`src/Aevatar.Presentation.AGUI/AGUIEventChannel.cs` |

## 3. 规范符合度（对照 AGENTS 顶级要求）

| 规则 | 结论 | 备注 |
|---|---|---|
| 严格分层 | 部分符合 | 主体分层清晰，但 Host Endpoint 仍承载较重编排与报告写入逻辑 |
| 统一投影链路 | 符合（按既定策略） | 统一 Pipeline 已落地；多 Run 不隔离是显式架构决策，不再视为缺陷 |
| 明确读写分离 | 符合 | `Command -> Event`，`Query -> ReadModel` 路径明确 |
| 依赖反转 | 部分符合 | 大部分依赖抽象；个别默认实现放在 Abstractions 且使用字符串反射 |
| 命名/namespace 一致性 | 符合 | 项目名、AssemblyName、RootNamespace 总体一致 |
| 无效层清理 | 基本符合 | CQRS/AGUI 已收敛；仍存在双 Host 能力重叠 |
| 可验证性 | 符合 | 全量 build/test 可通过 |

## 4. 主要发现（按严重度）

### D-01（架构决策，非缺陷）同一 Actor 多 Run 不隔离

- 决策：客户端不按 run 做全流过滤，消费该 Actor 的全量事件。
- 当前编码：
  - 请求结束条件按当前 `runId` 判断（`IsTerminalEventForRun`）：`src/Aevatar.Host.Api/Endpoints/ChatEndpoints.cs`
  - `RunErrorEvent` 已携带 `RunId`，用于请求级终止判断：`src/Aevatar.Presentation.AGUI/AGUIEvents.cs`
- 约束：这是产品语义选择，不再以“run 隔离缺失”作为审计问题。

### A-01（High，已整改）WorkflowLoopModule 多 Run 并发安全

- 现状：`WorkflowLoopModule` 使用活动 `runId` 集合管理并发执行，不再依赖单 `_currentRunId`。
- 证据：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs`

### A-02（High，已整改）AGUI 生命周期事件来源与 threadId 语义统一

- 现状：
  - API 不再主动写 `RunStartedEvent`。
  - `RunStartedEvent` 统一由 `StartWorkflowEvent` 投影产出。
  - `threadId` 统一取 `EventEnvelope.PublisherId`（即 ActorId）。
- 证据：`src/Aevatar.Host.Api/Endpoints/ChatEndpoints.cs`、`src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/EventEnvelopeToAGUIEventMapper.cs`

### A-03（Medium，已整改）Host.Api Endpoint 责任收敛

- 现状：`ChatEndpoints` 已下沉 Actor 解析、请求执行与投影收尾辅助流程，协议层只保留协议转换与写出。
- 证据：`src/Aevatar.Host.Api/Endpoints/ChatEndpoints.cs`

### A-04（Medium，已整改）Role 类型解析默认实现移出抽象层

- 现状：
  - `Aevatar.AI.Abstractions` 仅保留 `IRoleAgentTypeResolver` 接口。
  - 默认实现改为 `Aevatar.AI.Core.Agents.RoleGAgentTypeResolver`，并在组合层显式注册。
- 证据：`src/Aevatar.AI.Abstractions/Agents/IRoleAgentTypeResolver.cs`、`src/Aevatar.AI.Core/Agents/RoleGAgentTypeResolver.cs`、`src/Aevatar.Bootstrap/ServiceCollectionExtensions.cs`

### A-05（Medium，已整改）Host 协议入口去重

- 现状：`Host.Gateway` 不再暴露并行 Chat 协议端点，统一由 `Host.Api` 提供 Chat 协议。
- 证据：`src/Aevatar.Host.Gateway/Program.cs`、`src/Aevatar.Host.Gateway/README.md`

### A-06（Medium，已整改）Workflow 目录组织落地

- 现状：Workflow 相关项目统一在 `src/workflow/` 下，`sln/slnx` 路径同步更新。

### A-07（Low，已整改）流通道背压与错误可观测

- 现状：
  - `InMemoryStream` 改为有界通道，可配置满队列策略与订阅异常处理策略。
  - `AGUIEventChannel` 改为有界通道，满队列时显式失败而非静默丢弃。
- 证据：`src/Aevatar.Foundation.Runtime/Streaming/InMemoryStream.cs`、`src/Aevatar.Foundation.Runtime/Streaming/InMemoryStreamOptions.cs`、`src/Aevatar.Presentation.AGUI/AGUIEventChannel.cs`

## 5. 优点（建议保持）

- CQRS 抽象/内核/领域分层已形成稳定骨架，可扩展性较好。
- AGUI 与 Workflow 的耦合已从 Host 剥离到 Adapter，方向正确。
- `CaseProjection` demo 能证明“外部扩展 reducer/projector”路径可行。
- 项目命名与命名空间基本一致，`sln/slnx` 结构清晰。

## 6. 整改结果

- P0：已全部落地（契约文档、并发 run 状态管理、生命周期事件统一）。
- P1：已全部落地（`src/workflow/` 目录迁移、sln/slnx 与文档路径同步、Endpoint 责任收敛）。
- P2：已落地前两项（Host 定位收敛、流背压/错误策略）；架构守卫测试为后续增强项。

## 7. 复审结论

本轮整改后，审计问题 A-01 ~ A-07 已关闭，核心架构语义与实现对齐：  
1. 运行语义：同一 Actor 多 Run 不隔离已文档化并受测试保护。  
2. 结构语义：Workflow 目录组织、Host 协议职责、CQRS/AGUI 生命周期路径已统一。  
