# workflow_call 实践指南

## 概述

`workflow_call` 允许一个 workflow 调用另一个 workflow 作为子流程执行。支持多级嵌套（L1 → L2 → L3）、singleton/transient/scope 三种生命周期、以及 inline bundle 和 file-backed 两种定义来源。

## 快速体验

### 方式一：inline bundle（推荐起步）

通过 `/api/chat` 一次请求提交所有 YAML 定义，无需预注册。

```bash
curl -N -X POST http://localhost:5000/api/chat \
  -H "Content-Type: application/json" \
  -d '{
    "prompt": "hello from parent",
    "workflowYamls": [
      "name: parent_flow\nroles:\n  - id: main_role\n    name: MainRole\n    system_prompt: \"You are a helpful assistant.\"\nsteps:\n  - id: call_child\n    type: workflow_call\n    parameters:\n      workflow: \"child_flow\"\n  - id: format_output\n    type: transform\n    parameters:\n      op: \"trim\"",
      "name: child_flow\nroles:\n  - id: child_role\n    name: ChildRole\n    system_prompt: \"You summarize text concisely.\"\nsteps:\n  - id: summarize\n    type: llm_call\n    parameters:\n      role: \"child_role\""
    ]
  }'
```

**关键规则**：`workflowYamls` 数组中第一个 YAML 是入口 workflow，其余自动注册为 inline sub-workflow，可被 `workflow_call` 的 `workflow` 参数按 `name` 字段引用。

### 方式二：file-backed（生产部署）

将 YAML 文件放到 workflow 目录（配置于 `appsettings.json` → `WorkflowDefinitionFileSourceOptions.WorkflowDirectories`），应用启动时自动注册。

> **重要**：文件名（不含扩展名）必须与 YAML 内部的 `name:` 字段一致，否则 `workflow_call` 在校验阶段会因名称不匹配而失败。

示例目录结构：
```
workflows/
  parent_flow.yaml        # name: parent_flow
  child_flow.yaml         # name: child_flow
  grandchild_flow.yaml    # name: grandchild_flow
```

## YAML 定义格式

### 父 workflow（parent_flow.yaml）

```yaml
name: parent_flow
description: 父 workflow，调用子 workflow 处理核心逻辑
roles:
  - id: orchestrator
    name: Orchestrator
    system_prompt: "You orchestrate multi-step tasks."
steps:
  - id: call_child
    type: workflow_call
    parameters:
      workflow: "child_flow"
      lifecycle: "singleton"     # 可选：singleton（默认）/ transient / scope

  - id: finalize
    type: transform
    parameters:
      op: "trim"
```

### 子 workflow（child_flow.yaml）

```yaml
name: child_flow
description: 子 workflow，执行具体任务
roles:
  - id: worker
    name: Worker
    system_prompt: "You perform detailed analysis."
steps:
  - id: analyze
    type: llm_call
    parameters:
      role: "worker"
```

### 多级嵌套（child_flow 再调 grandchild_flow）

```yaml
name: child_flow
steps:
  - id: call_grandchild
    type: workflow_call
    parameters:
      workflow: "grandchild_flow"
      lifecycle: "transient"

  - id: process_result
    type: transform
    parameters:
      op: "trim"
```

## lifecycle 参数说明

| 值 | 行为 | 适用场景 |
|---|---|---|
| `singleton`（默认）| 同一 workflow 定义复用同一个子 actor | 重复调用同一子 workflow、需要保持子 workflow 状态 |
| `transient` | 每次调用创建新 actor，完成后自动销毁 | 无状态的一次性子任务 |
| `scope` | 每次调用创建新 actor，作用域内复用 | 预留，当前行为同 transient |

## API 交互

### HTTP SSE（推荐调试）

```bash
# 启动 workflow
curl -N -X POST http://localhost:5000/api/chat \
  -H "Content-Type: application/json" \
  -d '{
    "prompt": "Analyze this: hello world",
    "workflow": "parent_flow"
  }'
```

响应是 SSE 事件流，包含：
- `aevatar.run.context` — actorId、commandId
- `aevatar.text.delta` — 文本流式输出
- `aevatar.text.end` — 文本输出结束
- `aevatar.run.completed` — 执行完成

### WebSocket

```javascript
const ws = new WebSocket("ws://localhost:5000/api/ws/chat");

ws.onopen = () => {
  ws.send(JSON.stringify({
    type: "chat.command",
    requestId: "req-1",
    payload: {
      prompt: "hello",
      workflow: "parent_flow"
    }
  }));
};

ws.onmessage = (event) => {
  const msg = JSON.parse(event.data);
  // msg.type: "command.ack" | "agui.event" | "command.error"
  console.log(msg);
};
```

## 现有 Demo：多级调用

仓库中已有完整多级 workflow_call demo：

```
demos/Aevatar.Demos.Workflow/workflows/
  workflow_call_multilevel.yaml    # L0: 调用 subworkflow_level1
  subworkflow_level1.yaml          # L1: 调用 subworkflow_level2
  subworkflow_level2.yaml          # L2: 调用 subworkflow_level3
  subworkflow_level3.yaml          # L3: 叶子节点，执行 trim
```

可以直接用于验证多级 workflow_call 链路：

```bash
curl -N -X POST http://localhost:5000/api/chat \
  -H "Content-Type: application/json" \
  -d '{"prompt": "test multilevel call", "workflow": "workflow_call_multilevel"}'
```

## 调试排查

### 常见失败及原因

| 错误 | 原因 |
|---|---|
| `workflow_call missing workflow parameter` | `parameters.workflow` 未指定或为空 |
| `workflow_call lifecycle must be singleton/transient/scope` | `lifecycle` 拼写错误 |
| `definition snapshot name mismatch` | 文件名与 YAML 内部 `name:` 不一致 |
| `timed out waiting for definition resolution after 30000ms` | 目标 workflow 定义未注册或 definition actor 不可达 |
| `WorkflowGAgent is not bound to a workflow definition` | 目标定义 actor 存在但未绑定 YAML |

### 查看执行日志

关注日志中以下关键词：
- `SubWorkflowInvokeRequested` — 父 workflow 发出调用请求
- `SubWorkflowDefinitionResolveRequested` — 向定义 actor 请求 YAML
- `SubWorkflowDefinitionResolved` — 定义解析成功
- `SubWorkflowInvocationRegistered` — 子 actor 创建/复用成功
- `SubWorkflowInvocationCompleted` — 子 workflow 执行完成
- `workflow_call definition snapshot name mismatch` — 名称不匹配

## 注意事项

1. **YAML name 必须与注册名一致**：file-backed 模式下文件名即注册名；inline bundle 模式下 `name:` 字段即注册名。`workflow_call` 在校验阶段会解析 YAML 并比对名称。
2. **inline workflow 自动传递**：父 workflow 的所有 inline sub-workflow 定义会自动传递给子 workflow，支持跨级引用。
3. **definition resolution 有 30s 超时**：如果 definition actor 在 30 秒内未响应，调用将以超时失败。
4. **singleton 绑定与定义版本关联**：定义更新后 singleton 子 actor 会自动 rebind 到新版本，但不会热替换正在执行的 run。
5. **子 workflow 完成后自动清理**：transient/scope 子 actor 在完成后会自动 unlink + destroy；singleton 子 actor 会保留以供复用。
