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

### A-01（High）WorkflowLoopModule 仅支持单活动 Run

- 证据：
  - 模块内部仅一个 `_currentRunId`：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:20`
  - 启动新 run 时直接覆盖：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:66`
  - 完成事件仅按该字段比较：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:74`
- 影响：同一 Workflow Actor 被并发调用时，先前 run 可能被“误忽略”。

### A-02（High）AGUI 运行生命周期事件存在重复与标识不一致

- 证据：
  - API 在请求开始时主动写 `RunStartedEvent`：`src/Aevatar.Host.Api/Endpoints/ChatEndpoints.cs:146`
  - Mapper 在 `StartWorkflowEvent` 时再次产出 `RunStartedEvent`：`src/Aevatar.Workflow.Presentation.AGUIAdapter/EventEnvelopeToAGUIEventMapper.cs:34`
  - 两者 `threadId` 语义不一致：前者用 `actor.Id`，后者用 `evt.WorkflowName`：`src/Aevatar.Host.Api/Endpoints/ChatEndpoints.cs:149`, `src/Aevatar.Workflow.Presentation.AGUIAdapter/EventEnvelopeToAGUIEventMapper.cs:40`
- 影响：前端可能收到重复启动事件，线程标识存在歧义。

### A-03（Medium）Host.Api Endpoint 责任偏重

- 证据：单个 `HandleChat` 同时负责 Actor 创建/复用、协议输出、投影编排、报告写盘：`src/Aevatar.Host.Api/Endpoints/ChatEndpoints.cs:82`
- 影响：测试替身复杂，协议层变更容易牵动编排逻辑。

### A-04（Medium）默认 Role 类型解析实现放在 Abstractions 且采用字符串反射

- 证据：`ReflectionRoleAgentTypeResolver` 位于抽象项目并硬编码 `"Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core"`：`src/Aevatar.AI.Abstractions/Agents/IRoleAgentTypeResolver.cs:10`
- 影响：抽象层混入默认策略实现，运行期失败面增大（装配缺失时才暴露）。

### A-05（Medium）`Host.Api` 与 `Host.Gateway` 功能重叠，协议演进存在漂移风险

- 证据：
  - `Host.Api` 提供 `/api/chat`（SSE/WS）与 runs query：`src/Aevatar.Host.Api/Endpoints/ChatEndpoints.cs:51`
  - `Host.Gateway` 也实现独立 chat + stream 协议：`src/Aevatar.Host.Gateway/ChatEndpoints.cs:22`, `src/Aevatar.Host.Gateway/ChatEndpoints.cs:49`
- 影响：两套端点语义长期并行，维护成本和行为偏差风险上升。

### A-06（Medium）Workflow 领域项目组织可读性不足（建议建立 `src/workflow/`）

- 现状：
  - Workflow 相关项目分散在 `src/` 顶层：`Aevatar.Workflow.Core`、`Aevatar.Workflow.Projection`、`Aevatar.Workflow.Presentation.AGUIAdapter`
- 影响：
  - 领域边界在目录层不直观，长期增加维护与 onboarding 成本。
- 建议目标结构（含命名）：
  - `src/workflow/Aevatar.Workflow.Core`
  - `src/workflow/Aevatar.Workflow.Projection`（由 `Aevatar.Workflow.Projection` 迁移）
  - `src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter`（由 `Aevatar.Workflow.Presentation.AGUIAdapter` 迁移）

### A-07（Low）InMemory Stream 策略偏 best-effort，缺少背压与错误升级

- 证据：
  - 使用无界 Channel：`src/Aevatar.Foundation.Runtime/Streaming/InMemoryStream.cs:14`
  - 订阅处理异常被吞掉：`src/Aevatar.Foundation.Runtime/Streaming/InMemoryStream.cs:101`
  - AGUI 事件通道也为无界：`src/Aevatar.Presentation.AGUI/AGUIEventChannel.cs:16`
- 影响：高压场景可能内存膨胀，错误可观测性不足。

## 5. 优点（建议保持）

- CQRS 抽象/内核/领域分层已形成稳定骨架，可扩展性较好。
- AGUI 与 Workflow 的耦合已从 Host 剥离到 Adapter，方向正确。
- `CaseProjection` demo 能证明“外部扩展 reducer/projector”路径可行。
- 项目命名与命名空间基本一致，`sln/slnx` 结构清晰。

## 6. 整改路线图（建议）

### P0（必须先做）

1. 把“同一 Actor 多 Run 不隔离”写成正式契约（README + API 文档 + 测试断言），避免后续被误改。  
2. `WorkflowLoopModule` 改为按 runId 管理内部状态（至少支持同 Actor 多 run 并行安全）。  
3. 统一生命周期起始事件来源与 threadId 语义，消除重复 `RunStartedEvent`。  

### P1（高优）

1. 建立 `src/workflow/` 目录并迁移 Workflow 相关项目。  
2. 同步项目重命名、AssemblyName、RootNamespace、`sln/slnx` 引用与文档路径。  
3. 把 `HandleChat` 的编排与报告逻辑下沉到应用服务，Endpoint 仅做协议转换。  

### P2（优化）

1. 明确 `Host.Api` 与 `Host.Gateway` 的定位（合并、降级或分场景隔离）。  
2. 为生产流实现引入可配置背压、队列上限和错误上报策略。  
3. 增加自动化架构守卫测试（依赖方向、命名规则、关键约束）。  

## 7. 审计结论

当前架构已具备可维护骨架，且“同一 Actor 多 Run 不隔离”已明确为产品语义。  
下一阶段重点应放在两件事：  
1. 运行正确性：修复 Workflow 并发 run 下的状态管理缺口。  
2. 结构清晰性：落地 `src/workflow/` 目录与对应项目命名重构。  
