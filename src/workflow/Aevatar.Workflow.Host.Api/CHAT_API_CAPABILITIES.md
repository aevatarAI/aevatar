# Workflow Chat APIs Capability Guide

本文档描述当前框架层 `POST /api/chat` 与 `GET /api/ws/chat` 的能力边界、路由语义与 Human-in-the-Loop 交互协议。

## 1. Endpoint 总览

| Endpoint | 协议 | 用途 |
|---|---|---|
| `POST /api/chat` | HTTP + SSE | 提交一次 chat/workflow run，请求-响应同连接持续推送事件帧 |
| `GET /api/ws/chat` | WebSocket | 提交一次 chat command，回包 `ack + 事件流`，更适合长连接客户端 |
| `POST /api/workflows/resume` | HTTP JSON | 恢复 `human_input/human_approval` 挂起步骤 |
| `POST /api/workflows/signal` | HTTP JSON | 向等待信号的步骤发送外部 signal |

`/api/chat` 与 `/api/ws/chat` 走同一套应用层执行引擎，差异仅在协议封装（SSE vs WebSocket）。

## 2. Chat 输入模型（两端点共用）

```json
{
  "prompt": "用户输入",
  "workflow": "direct|auto|auto_review|<自定义名称>",
  "agentId": "可选，复用已有 Workflow Actor",
  "workflowYaml": "可选，inline YAML"
}
```

字段语义：

- `prompt`：必填，当前轮输入。
- `workflow`：可选，按名称从 registry 取 workflow。
- `workflowYaml`：可选，inline 提交 YAML（会先做 parse/validate）。
- `agentId`：可选，复用指定 actor 继续执行。

## 3. Workflow 路由与绑定规则

执行选择优先级：

1. `workflowYaml`（inline）
2. `workflow`（名称）
3. 默认 workflow（`WorkflowRunBehaviorOptions.DefaultWorkflowName`，默认 `direct`）

补充规则：

- 当 `workflow` 与 `workflowYaml.name` 同时提供且不一致，返回 `WORKFLOW_NAME_MISMATCH`。
- 当带 `agentId` 复用已有 actor 时，不允许切到另一个 workflow；不一致返回 `WORKFLOW_BINDING_MISMATCH`。
- 传 `agentId` 但该 actor 未配置 workflow，且又未给 `workflowYaml`，返回 `AGENT_WORKFLOW_NOT_CONFIGURED`。
- 默认配置下，`workflow/workflowYaml` 都为空会走 `direct`；可通过 `UseAutoAsDefaultWhenWorkflowUnspecified=true` 切换默认 `auto`。

## 4. 内建“按 command 自动编排”能力

框架默认注册内建 workflow：`direct`、`auto`、`auto_review`。

### `direct`

- 单步 `llm_call`，直接回答用户问题。

### `auto`

核心流程（内建 step）：

1. `capture_input`：记录原始 `prompt`
2. `classify`：让 planner 判断“直接回答”还是“生成 YAML”
3. `check_is_yaml`：判断输出是否为 YAML 代码块
4. 若是 YAML：`show_for_approval`（`human_approval`）
5. 拒绝：`refine_yaml` 后回到审批（可多轮）
6. 通过：`extract_and_execute`（`dynamic_workflow`）直接执行定稿 YAML

也就是说，`auto` 支持“根据 command 自己写 workflow + 等 human approval + 批准后自动执行”。

### `auto_review`

与 `auto` 一样支持“自动生成 + 多轮审批/优化”，区别是：

- 审批通过后不自动执行动态 workflow，只产出最终 YAML（用于手动运行）。

这对应“先定稿，再显式 Run Final Workflow”场景。

## 5. Human-in-the-Loop 交互协议

当执行到 `human_input` 或 `human_approval` 时，事件流会发出 `HUMAN_INPUT_REQUEST`，包含：

- `runId`
- `stepId`
- `suspensionType`
- `prompt`
- `timeoutSeconds`
- `metadata`

客户端需要调用恢复端点：

### 恢复挂起（审批/输入）

`POST /api/workflows/resume`

```json
{
  "actorId": "Workflow:xxxx",
  "runId": "run-xxxx",
  "stepId": "show_for_approval",
  "commandId": "可选，建议显式传",
  "approved": true,
  "userInput": "可选反馈",
  "metadata": {
    "source": "ui"
  }
}
```

### 发送外部信号

`POST /api/workflows/signal`

```json
{
  "actorId": "Workflow:xxxx",
  "runId": "run-xxxx",
  "signalName": "continue",
  "commandId": "可选，建议显式传",
  "payload": "任意字符串"
}
```

> 推荐在客户端显式携带 `actorId + runId (+ stepId/signalName)`，不依赖服务端内存映射。

## 6. 输出事件能力（SSE 与 WS 一致）

统一输出 `WorkflowOutputFrame`，核心 `Type` 包括：

- `RUN_STARTED` / `RUN_FINISHED` / `RUN_ERROR`
- `STEP_STARTED` / `STEP_FINISHED`
- `TEXT_MESSAGE_START` / `TEXT_MESSAGE_CONTENT` / `TEXT_MESSAGE_END`
- `TOOL_CALL_START` / `TOOL_CALL_END`
- `HUMAN_INPUT_REQUEST`
- `STATE_SNAPSHOT`
- `CUSTOM`

常见 `CUSTOM` 事件：

- `aevatar.run.context`（开局回传 `actorId/workflowName/commandId`）
- `aevatar.step.request`
- `aevatar.step.completed`
- `aevatar.workflow.waiting_signal`
- `aevatar.llm.reasoning`（LLM 思考增量）

## 7. WebSocket 命令协议

`GET /api/ws/chat` 建连后，客户端发送单条命令：

```json
{
  "type": "chat.command",
  "requestId": "client-request-id",
  "payload": {
    "prompt": "用户输入",
    "workflow": "auto_review"
  }
}
```

服务端返回：

- `command.ack`：接受成功，返回 `commandId/actorId/workflow`
- `agui.event`：逐帧事件（payload 即 `WorkflowOutputFrame`）
- `command.error`：请求不合法或 run 启动失败

## 8. 错误码与启动失败语义

常见错误码：

- `AGENT_NOT_FOUND`
- `WORKFLOW_NOT_FOUND`
- `AGENT_TYPE_NOT_SUPPORTED`
- `WORKFLOW_BINDING_MISMATCH`
- `AGENT_WORKFLOW_NOT_CONFIGURED`
- `INVALID_WORKFLOW_YAML`
- `WORKFLOW_NAME_MISMATCH`

HTTP 语义：

- 400：输入不合法（例如 YAML 解析失败）
- 404：actor/workflow 不存在
- 409：workflow 绑定冲突
- 503：projection 不可用

## 9. 最常用三种调用模式

### A. 直接问答

```json
{ "prompt": "解释一下 CQRS", "workflow": "direct" }
```

### B. 自动判断并走审批，审批后自动执行

```json
{ "prompt": "帮我设计一个多角色内容生产流程", "workflow": "auto" }
```

### C. 自动判断并走审批，审批后只定稿 YAML

```json
{ "prompt": "帮我设计一个多语言本地化工作流", "workflow": "auto_review" }
```

---

实现位置（代码）：

- Endpoint 与交互协议：`Aevatar.Workflow.Infrastructure/CapabilityApi/*`
- 运行编排：`Aevatar.Workflow.Application/Runs/*`
- 内建 `auto/auto_review` 定义：`Aevatar.Workflow.Application/Workflows/WorkflowDefinitionRegistry.cs`
