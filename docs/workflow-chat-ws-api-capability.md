# Workflow Chat API 能力说明（框架层）

> 单一事实源（Single Source of Truth）：`/api/chat`、`/api/ws/chat` 相关能力说明以本文为准。  
> Host 侧入口文档：`src/workflow/Aevatar.Workflow.Host.Api/README.md`、`src/workflow/Aevatar.Workflow.Host.Api/CHAT_API_CAPABILITIES.md`。

本文档面向框架使用者，说明当前 `POST /api/chat` 与 `GET /api/ws/chat` 可以做什么，尤其是：

- 根据 `prompt` 自动判断是否要生成 workflow
- 在 `human_approval` 节点等待人工确认
- 支持多轮“反馈 -> 重新生成 -> 再审批”
- 审批通过后自动执行（`auto`）或只定稿不执行（`auto_review`）

## 1. 端点与职责

| Endpoint | 协议 | 作用 |
|---|---|---|
| `POST /api/chat` | HTTP + SSE | 发起一次 run，并持续接收运行时 envelope 投影流 |
| `GET /api/ws/chat` | WebSocket | 与 `/api/chat` 同能力，使用 WS 封装 |
| `POST /api/workflows/resume` | HTTP JSON | 恢复 `human_input/human_approval` 挂起步骤 |
| `POST /api/workflows/signal` | HTTP JSON | 向等待信号的步骤发送 signal |

说明：`/api/chat` 与 `/api/ws/chat` 走同一套执行链路，差别只有传输协议。

口径补充：

- API 输入会先规范化为应用命令模型，再包装成 `EventEnvelope` 投递到目标 Actor。
- 这里的 `EventEnvelope` 是 runtime message envelope，不等于 Event Sourcing 的领域事件记录。

## 2. 输入模型（chat）

```json
{
  "prompt": "用户输入，必填",
  "workflow": "可选：已注册 workflow 名称（内建 + 文件加载）",
  "agentId": "可选：复用已有 Workflow Actor",
  "workflowYamls": ["可选：inline YAML bundle（数组）"]
}
```

选择优先级：

1. `workflowYamls`（inline bundle，首项为入口 workflow）
2. `workflow`（已注册 workflow 名称 lookup）
3. 当 `workflow/workflowYamls` 都为空且未提供 `agentId` 时，外部 API 边界默认路由到 `auto`
4. 当只提供 `agentId` 且 `workflow/workflowYamls` 为空时，保持 workflow 未指定，复用 actor 已绑定 workflow

契约约束：

- `workflow` 只表示“按名称查找已注册 workflow”（内建 + 文件加载）。
- `workflowYamls` 只表示“inline YAML bundle”，不承担名称查找语义。
- 若同时传 `workflow` 与 `workflowYamls`，以 `workflowYamls` 为准。
- `direct/auto/auto_review` 可显式传入，按注册表解析，不要求存在同名文件。

## 3. 自动编排能力（按 prompt 决策）

框架内建了 `direct`、`auto`、`auto_review` 三个 workflow（内部能力）。

### `direct`

- 直接 `llm_call` 输出答案。

### `auto`

典型链路：

1. `classify`：判断“直接回答”还是“输出 YAML”
2. 非 YAML：直接回答用户问题并结束本次 run（不进入 YAML 校验/审批）
3. YAML：先经过 `workflow_yaml_validate` 校验
4. 校验失败：走 `refine_yaml` 继续修正
5. 校验成功：进入 `human_approval`
6. 人工拒绝：走 `refine_yaml`，再次审批（可多轮）
7. 人工通过：`dynamic_workflow` 执行定稿 YAML

结论：`auto` 支持“根据 prompt 自动写 workflow + 强制校验 + 人工审批 + 通过即执行”。

### `auto_review`

和 `auto` 相同地支持“自动生成 + 强制校验 + 多轮审批优化”，但审批通过后：

- 不自动执行，只输出最终 YAML（适合手动触发最终 run）。

## 4. Human Approval / Human Input 如何继续

当 run 到 `human_input` 或 `human_approval`，运行时 envelope 投影流会发出 `HUMAN_INPUT_REQUEST`，包含：

- `runId`
- `stepId`
- `prompt`
- `suspensionType`
- `metadata`

客户端拿到这些字段后，调用恢复接口：

```json
POST /api/workflows/resume
{
  "actorId": "Workflow:xxx",
  "runId": "run-xxx",
  "stepId": "show_for_approval",
  "approved": false,
  "userInput": "这里是优化建议",
  "commandId": "建议传，便于串联同一轮交互"
}
```

如果某些流程在等待外部信号，再调用：

```json
POST /api/workflows/signal
{
  "actorId": "Workflow:xxx",
  "runId": "run-xxx",
  "signalName": "continue",
  "payload": "任意字符串",
  "commandId": "建议传"
}
```

实践建议：显式传递 `actorId + runId (+ stepId)`，不要依赖服务端内存映射。

## 5. 输出事件（SSE/WS）

统一输出 `WorkflowOutputFrame`，核心事件类型包括：

- `RUN_STARTED` / `RUN_FINISHED` / `RUN_ERROR`
- `STEP_STARTED` / `STEP_FINISHED`
- `TEXT_MESSAGE_START` / `TEXT_MESSAGE_CONTENT` / `TEXT_MESSAGE_END`
- `HUMAN_INPUT_REQUEST`
- `TOOL_CALL_START` / `TOOL_CALL_END`
- `STATE_SNAPSHOT`
- `CUSTOM`

常见 `CUSTOM` 事件：

- `aevatar.run.context`：回传 `actorId/workflowName/commandId`
- `aevatar.step.request`、`aevatar.step.completed`
- `aevatar.llm.reasoning`：LLM 思考过程增量
- `aevatar.workflow.waiting_signal`

## 6. WebSocket 请求/回包协议

连接 `GET /api/ws/chat` 后，发送：

```json
{
  "type": "chat.command",
  "requestId": "client-req-1",
  "payload": {
    "prompt": "帮我设计一个多角色发布流程"
  }
}
```

服务端回包类型：

- `command.ack`：返回 `commandId/actorId/workflow`
- `agui.event`：逐帧业务事件（payload 即 `WorkflowOutputFrame`）
- `command.error`：输入或启动阶段错误

## 7. 常见使用模式

### 模式 A：直接对话

```json
{ "prompt": "解释一下 event sourcing" }
```

### 模式 B：自动生成 + 审批 + 自动执行

```json
{ "prompt": "设计一个内容生产流水线，包含并行校对与质量门禁", "workflow": "auto" }
```

### 模式 C：自动生成 + 审批 + 只定稿

```json
{ "prompt": "设计一个多语言本地化流程，先不要执行", "workflow": "auto_review" }
```

### 模式 D：inline 多工作流 bundle（支持 workflow_call 子流程）

```json
{
  "prompt": "执行 inline 流程",
  "workflowYamls": [
    "name: root\nroles: ...\nsteps: ...",
    "name: child_a\nroles: ...\nsteps: ...",
    "name: child_b\nroles: ...\nsteps: ..."
  ]
}
```

---

参考实现：

- `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/*`
- `src/workflow/Aevatar.Workflow.Application/Runs/*`
- `src/workflow/Aevatar.Workflow.Application/Workflows/WorkflowDefinitionRegistry.cs`
