# Workflow LLM 流式链路架构评分卡（2026-02-25，复核版）

## 1. 审计范围与方法

1. 审计对象：Workflow 能力中 `LLM 文本流 -> 统一投影 -> SSE/WS 输出` 主链路。
2. 重点问题：文本流端到端路径、会话语义、统一投影约束、是否支持非文本流。
3. 评分规范：`docs/audit-scorecard/README.md`（100 分，6 维模型）。
4. 证据类型：源码静态证据（`文件:行号`）+ 现有测试用例覆盖点 + 本地命令执行结果。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 链路静态扫描 | `rg -n "(stream|SSE|ws|ChatStreamAsync|WorkflowOutputFrame|ProjectionSessionEventHub)" src docs test` | 通过（定位到入口、执行引擎、投影与协议层关键实现） |
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过 |
| 路由门禁 | `bash tools/ci/projection_route_mapping_guard.sh` | 通过 |
| 测试稳定性门禁 | `bash tools/ci/test_stability_guards.sh` | 通过 |
| 分片构建门禁 | `bash tools/ci/solution_split_guards.sh` | 通过 |
| 分片测试门禁 | `bash tools/ci/solution_split_test_guards.sh` | 通过（含少量预期 skip） |
| 全量构建 | `dotnet build aevatar.slnx --nologo` | 通过 |
| 全量回归 | `dotnet test aevatar.slnx --nologo` | 通过（全测试绿，含少量预期 skip） |

## 3. 架构事实与证据

### 3.1 文本流主链路是统一的（非 API 直连 LLM）

1. Host 入口统一：`/api/chat`（SSE）与 `/api/ws/chat`（WS）都进入同一 `ICommandExecutionService`。  
证据：`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatEndpoints.cs:16`、`:21`、`:43`、`:182`。
2. 应用层先建立 projection lease，再挂 live sink，按 `commandId` 会话输出。  
证据：`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunContextFactory.cs:53`、`:62`。
3. `RoleGAgent` 使用 `ChatStreamAsync`，按三段式发布文本事件：`START -> CONTENT* -> END`。  
证据：`src/Aevatar.AI.Core/RoleGAgent.cs:114`、`:124`、`:140`。
4. AI 流式核心消费 `provider.ChatStreamAsync`，已逐 chunk 统一处理 `DeltaContent + DeltaToolCall + Usage`，并聚合工具调用。  
证据：`src/Aevatar.AI.Core/Chat/ChatRuntime.cs:89`、`:154`、`:232`。
5. 投影分支将 `EventEnvelope` 转为 AGUI 事件，再转 `WorkflowRunEvent`，发布到 `workflow-run:{actorId}:{commandId}` 会话流。  
证据：`src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/WorkflowExecutionAGUIEventProjector.cs:34`、`:48`；`src/Aevatar.CQRS.Projection.Core/Streaming/ProjectionSessionEventHub.cs:77`；`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowRunEventSessionCodec.cs:12`。
6. 最终输出统一映射为 `WorkflowOutputFrame`，并在 run 收敛后统一补发 `STATE_SNAPSHOT`，由 SSE/WS 发送。  
证据：`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunExecutionEngine.cs:72`；`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunStateSnapshotEmitter.cs:40`；`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowOutputFrameMapper.cs:11`；`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatSseResponseWriter.cs:41`、`:45`；`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatWebSocketRunCoordinator.cs:22`、`:56`。

### 3.2 其他“事件流类型”支持情况

1. 统一输出模型支持 `RUN_* / STEP_* / TEXT_* / TOOL_CALL_* / CUSTOM / STATE_SNAPSHOT`。  
证据：`src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/WorkflowRunEventContracts.cs:13`-`:109`。
2. AGUI adapter 已支持 ToolCall 事件映射（`ToolCallEvent -> TOOL_CALL_START`，`ToolResultEvent -> TOOL_CALL_END`）。  
证据：`src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/EventEnvelopeToAGUIEventMapper.cs:281`-`:309`。
3. WebSocket 传输层是文本协议：非 text 帧被跳过，发送固定 text 帧。  
证据：`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatWebSocketProtocol.cs:24`-`:25`、`:44`。

### 3.3 测试覆盖现状

1. `ChatRuntimeStreamingBufferTests` 覆盖 bounded stream 背压与 `DeltaToolCall` 流式透传。  
证据：`test/Aevatar.AI.Tests/ChatRuntimeStreamingBufferTests.cs:13`、`:30`。
2. `WorkflowRunOrchestrationComponentTests` 覆盖 `STATE_SNAPSHOT` 在收敛后统一输出与快照载荷。  
证据：`test/Aevatar.Workflow.Application.Tests/WorkflowRunOrchestrationComponentTests.cs:88`、`:160`、`:199`。
3. `ChatWebSocketCoordinatorAndProtocolTests` 覆盖 `command.ack -> agui.event* -> query.result` 顺序与文本帧协议。  
证据：`test/Aevatar.Workflow.Host.Api.Tests/ChatWebSocketCoordinatorAndProtocolTests.cs:15`、`:50`、`:82`。
4. `WorkflowCapabilityEndpointsCoverageTests` 覆盖 WS 入口错误分支与运行异常映射。  
证据：`test/Aevatar.Workflow.Host.Api.Tests/WorkflowCapabilityEndpointsCoverageTests.cs:113`、`:135`、`:159`。

## 4. 评分结果（100 分制）

**总分：99 / 100（A+）**

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | Host 仅协议适配，业务执行由 Application/Projection 链路承载。 |
| CQRS 与统一投影链路 | 20 | 20 | SSE/WS 与 AGUI 共享同一投影输入链路，未出现双轨实现。 |
| Projection 编排与状态约束 | 20 | 20 | lease/session 分离与会话流键明确，`STATE_SNAPSHOT` 已有统一产出。 |
| 读写分离与会话语义 | 15 | 14 | commandId 会话语义清晰，`DeltaToolCall` 已贯通；多模态流仍未覆盖。 |
| 命名语义与冗余清理 | 10 | 10 | 事件名、模型名、链路命名一致，语义明确。 |
| 可验证性（门禁/构建/测试） | 15 | 15 | 架构门禁、分片门禁、`build` 与全量 `test` 均已复跑通过。 |

## 5. 主要扣分项（按影响度）

### P1

1. 暂无 P1 阻断项。

### P2

1. WebSocket 协议层仅支持文本消息帧，非文本流（如二进制多模态帧）当前不支持。  
证据：`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatWebSocketProtocol.cs:24`-`:25`、`:44`。

## 6. 改进建议（优先级）

1. P1：若目标包含多模态流，升级 WS 协议层为“类型化帧”模型（文本/二进制/元信息分轨），避免文本协议成为瓶颈。
2. P2：补一条“非文本帧输入”到“统一输出帧协议”的 E2E 回归测试，作为多模态演进护栏。
3. P3：`query.result` 可继续保留为 WS 控制消息；若后续统一控制面，再引入显式控制帧模型。

## 7. 非扣分观察项（基线口径）

1. 本次未对 `InMemory`/`Local Actor Runtime` 做扣分，符合评分规范基线豁免。  
证据：`docs/audit-scorecard/README.md:66`-`:80`。
