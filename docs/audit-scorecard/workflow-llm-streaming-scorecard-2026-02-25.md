# Workflow LLM 流式链路架构评分卡（2026-02-25）

## 1. 审计范围与方法

1. 审计对象：Workflow 能力中 `LLM 文本流 -> 统一投影 -> SSE/WS 输出` 主链路。
2. 重点问题：文本流端到端路径、会话语义、统一投影约束、是否支持非文本流。
3. 评分规范：`docs/audit-scorecard/README.md`（100 分，6 维模型）。
4. 证据类型：源码静态证据（`文件:行号`）+ 现有测试用例覆盖点 + 本地命令执行结果。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 链路静态扫描 | `rg -n "(stream|SSE|ws|ChatStreamAsync|WorkflowOutputFrame|ProjectionSessionEventHub)" src docs test` | 通过（定位到入口、执行引擎、投影与协议层关键实现） |
| 相关测试定位 | `nl -ba test/Aevatar.Workflow.Host.Api.Tests/*.cs` | 通过（已定位路由/SSE/WS/AGUI projector 相关断言） |
| 目标测试执行 | `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --filter "FullyQualifiedName~ChatEndpointsInternalTests|FullyQualifiedName~ChatWebSocketCoordinatorAndProtocolTests"` | 失败：沙箱内 MSBuild NamedPipe 权限错误（`SocketException (13): Permission denied`） |

说明：本次评分以静态架构证据为主，自动化运行结果受当前执行环境限制。

## 3. 架构事实与证据

### 3.1 文本流主链路是统一的（非 API 直连 LLM）

1. Host 入口统一：`/api/chat`（SSE）与 `/api/ws/chat`（WS）都进入同一 `ICommandExecutionService`。  
证据：`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatEndpoints.cs:17`、`:22`、`:44`、`:184`。
2. 应用层先建立 projection lease，再挂 live sink，按 `commandId` 会话输出。  
证据：`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunContextFactory.cs:53`、`:62`。
3. `RoleGAgent` 使用 `ChatStreamAsync`，按三段式发布文本事件：`START -> CONTENT* -> END`。  
证据：`src/Aevatar.AI.Core/RoleGAgent.cs:114`、`:125`、`:140`。
4. AI 流式核心消费 `provider.ChatStreamAsync`，当前逐 chunk 推送 `DeltaContent`。  
证据：`src/Aevatar.AI.Core/Chat/ChatRuntime.cs:152`、`:154`、`:157`。
5. 投影分支将 `EventEnvelope` 转为 AGUI 事件，再转 `WorkflowRunEvent`，发布到 `workflow-run:{actorId}:{commandId}` 会话流。  
证据：`src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/WorkflowExecutionAGUIEventProjector.cs:34`、`:48`；`src/Aevatar.CQRS.Projection.Core/Streaming/ProjectionSessionEventHub.cs:77`；`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowRunEventSessionCodec.cs:12`。
6. 最终输出统一映射为 `WorkflowOutputFrame`，由 SSE/WS 发送。  
证据：`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowOutputFrameMapper.cs:11`；`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatSseResponseWriter.cs:34`、`:45`；`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatWebSocketRunCoordinator.cs:24`、`:26`。

### 3.2 其他“事件流类型”支持情况

1. 统一输出模型支持 `RUN_* / STEP_* / TEXT_* / TOOL_CALL_* / CUSTOM / STATE_SNAPSHOT`。  
证据：`src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/WorkflowRunEventContracts.cs:13`-`:109`。
2. AGUI adapter 已支持 ToolCall 事件映射（`ToolCallEvent -> TOOL_CALL_START`，`ToolResultEvent -> TOOL_CALL_END`）。  
证据：`src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/EventEnvelopeToAGUIEventMapper.cs:281`-`:309`。
3. WebSocket 传输层是文本协议：非 text 帧被跳过，发送固定 text 帧。  
证据：`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatWebSocketProtocol.cs:24`-`:25`、`:44`。

### 3.3 测试覆盖现状（证据存在但运行受限）

1. `ChatEndpointsInternalTests` 覆盖路由注册、SSE 输出与命令错误映射。  
证据：`test/Aevatar.Workflow.Host.Api.Tests/ChatEndpointsInternalTests.cs:18`、`:69`、`:110`、`:185`。
2. `ChatWebSocketCoordinatorAndProtocolTests` 覆盖 `ack/agui.event/query.result` 协议顺序与 WS 协议行为。  
证据：`test/Aevatar.Workflow.Host.Api.Tests/ChatWebSocketCoordinatorAndProtocolTests.cs:16`、`:61`、`:97`、`:121`。
3. `WorkflowExecutionAGUIEventProjectorTests` 覆盖会话隔离（按 commandId）和全类型映射。  
证据：`test/Aevatar.Workflow.Host.Api.Tests/WorkflowExecutionAGUIEventProjectorTests.cs:18`、`:46`、`:151`、`:199`。

## 4. 评分结果（100 分制）

**总分：93 / 100（A）**

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | Host 仅协议适配，业务执行由 Application/Projection 链路承载。 |
| CQRS 与统一投影链路 | 20 | 20 | SSE/WS 与 AGUI 共享同一投影输入链路，未出现双轨实现。 |
| Projection 编排与状态约束 | 20 | 19 | lease/session 分离与会话流键明确；运行态管理整体符合约束。 |
| 读写分离与会话语义 | 15 | 13 | commandId 会话语义清晰，但流式能力与 LLM chunk 语义未完全对齐。 |
| 命名语义与冗余清理 | 10 | 10 | 事件名、模型名、链路命名一致，语义明确。 |
| 可验证性（门禁/构建/测试） | 15 | 11 | 有针对性测试用例，但本次环境无法完成 `dotnet test` 实跑验证。 |

## 5. 主要扣分项（按影响度）

### P1

1. 暂无 P1 阻断项。

### P2

1. LLM 流式 chunk 的 `DeltaToolCall` 在当前主链路未消费，流式工具调用语义可能丢失。  
证据：`src/Aevatar.AI.Abstractions/LLMProviders/LLMResponse.cs:34`；`src/Aevatar.AI.Core/Chat/ChatRuntime.cs:154`-`:159`（仅处理 `DeltaContent`）。
2. WebSocket 协议层仅支持文本消息帧，非文本流（如二进制多模态帧）当前不支持。  
证据：`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatWebSocketProtocol.cs:24`-`:25`、`:44`。
3. `STATE_SNAPSHOT` 已定义在统一模型，但当前主链路缺少对应 envelope->AGUI 生产分支，能力处于“协议可表示、业务未产出”状态。  
证据：`src/Aevatar.Presentation.AGUI/AGUIEvents.cs:82`；`src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/WorkflowRunEventContracts.cs:77`；`src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/EventEnvelopeToAGUIEventMapper.cs:49`-`:313`（未见 state snapshot producer handler）。

### P3

1. 本次目标测试执行受环境权限限制，导致“文档中的通过结论”无法用当次命令闭环复验。  
证据：命令 `dotnet test ...` 返回 `SocketException (13): Permission denied`（MSBuild NamedPipe）。

## 6. 改进建议（优先级）

1. P1：为 `ChatRuntime.ChatStreamAsync` 增加 `DeltaToolCall` 处理分支，并统一映射为 `TOOL_CALL_*` 事件（与非流式 tool loop 语义对齐）。
2. P1：若目标包含多模态流，升级 WS 协议层为“类型化帧”模型（文本/二进制/元信息分轨），避免文本协议成为瓶颈。
3. P2：补充 `STATE_SNAPSHOT` 生产规则（明确由哪个 projector/handler 在何时产出），或者删除未落地协议项以降低语义漂移。
4. P2：在可执行环境补跑 `ChatEndpointsInternalTests` 与 `ChatWebSocketCoordinatorAndProtocolTests`，把结果回填到本评分卡。

## 7. 非扣分观察项（基线口径）

1. 本次未对 `InMemory`/`Local Actor Runtime` 做扣分，符合评分规范基线豁免。  
证据：`docs/audit-scorecard/README.md:66`-`:80`。
