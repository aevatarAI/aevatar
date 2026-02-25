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
| 目标测试执行（AI） | `dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo` | 通过（48/48） |
| 目标测试执行（Application） | `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo` | 通过（39/39） |
| 目标测试执行（Host API） | `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo` | 通过（160/160） |
| 全量回归 | `dotnet test aevatar.slnx --nologo` | 通过（全测试绿，含少量预期 skip） |

## 3. 架构事实与证据

### 3.1 文本流主链路是统一的（非 API 直连 LLM）

1. Host 入口统一：`/api/chat`（SSE）与 `/api/ws/chat`（WS）都进入同一 `ICommandExecutionService`。  
证据：`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatEndpoints.cs:17`、`:22`、`:44`、`:184`。
2. 应用层先建立 projection lease，再挂 live sink，按 `commandId` 会话输出。  
证据：`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunContextFactory.cs:53`、`:62`。
3. `RoleGAgent` 使用 `ChatStreamAsync`，按三段式发布文本事件：`START -> CONTENT* -> END`。  
证据：`src/Aevatar.AI.Core/RoleGAgent.cs:114`、`:125`、`:140`。
4. AI 流式核心消费 `provider.ChatStreamAsync`，已逐 chunk 统一处理 `DeltaContent + DeltaToolCall + Usage`，并聚合工具调用。  
证据：`src/Aevatar.AI.Core/Chat/ChatRuntime.cs:89`。
5. 投影分支将 `EventEnvelope` 转为 AGUI 事件，再转 `WorkflowRunEvent`，发布到 `workflow-run:{actorId}:{commandId}` 会话流。  
证据：`src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/WorkflowExecutionAGUIEventProjector.cs:34`、`:48`；`src/Aevatar.CQRS.Projection.Core/Streaming/ProjectionSessionEventHub.cs:77`；`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowRunEventSessionCodec.cs:12`。
6. 最终输出统一映射为 `WorkflowOutputFrame`，并在 run 收敛后统一补发 `STATE_SNAPSHOT`，由 SSE/WS 发送。  
证据：`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunExecutionEngine.cs:72`；`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowOutputFrameMapper.cs:11`；`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatSseResponseWriter.cs:34`、`:45`；`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatWebSocketRunCoordinator.cs:23`、`:25`。

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

**总分：98 / 100（A）**

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | Host 仅协议适配，业务执行由 Application/Projection 链路承载。 |
| CQRS 与统一投影链路 | 20 | 20 | SSE/WS 与 AGUI 共享同一投影输入链路，未出现双轨实现。 |
| Projection 编排与状态约束 | 20 | 20 | lease/session 分离与会话流键明确，`STATE_SNAPSHOT` 已有统一产出。 |
| 读写分离与会话语义 | 15 | 14 | commandId 会话语义清晰，`DeltaToolCall` 已贯通；多模态流仍未覆盖。 |
| 命名语义与冗余清理 | 10 | 10 | 事件名、模型名、链路命名一致，语义明确。 |
| 可验证性（门禁/构建/测试） | 15 | 14 | `build + 分层测试 + 全量测试` 已通过；架构门禁待按 CI 脚本额外复跑。 |

## 5. 主要扣分项（按影响度）

### P1

1. 暂无 P1 阻断项。

### P2

1. WebSocket 协议层仅支持文本消息帧，非文本流（如二进制多模态帧）当前不支持。  
证据：`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatWebSocketProtocol.cs:24`-`:25`、`:44`。

### P3

1. `query.result` 仍属于 WS 专用控制消息，当前仅承载收敛状态，不是统一 `WorkflowRunEvent` 事件类型。  
证据：`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatWebSocketRunCoordinator.cs:54`。

## 6. 改进建议（优先级）

1. P1：若目标包含多模态流，升级 WS 协议层为“类型化帧”模型（文本/二进制/元信息分轨），避免文本协议成为瓶颈。
2. P2：将 `query.result` 收敛控制消息与统一事件流进一步解耦（例如引入统一控制帧模型）。
3. P2：补跑并固化架构门禁脚本（`architecture_guards.sh`、`projection_route_mapping_guard.sh`）到本次变更记录。

## 7. 非扣分观察项（基线口径）

1. 本次未对 `InMemory`/`Local Actor Runtime` 做扣分，符合评分规范基线豁免。  
证据：`docs/audit-scorecard/README.md:66`-`:80`。
