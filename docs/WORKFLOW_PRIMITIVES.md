# Workflow Primitives 参考手册

本文按原语逐条说明：

- 作用（这个原语做什么）
- 常用参数（最常用的配置项）
- Sample（最小 YAML 片段）

> 约定：示例中 `parameters` 的值统一使用字符串；`target_role` 与 `role` 为别名，推荐优先使用 `target_role`。

## 1. 通用写法

```yaml
name: my_workflow
description: demo
roles:
  - id: assistant
    name: Assistant
    system_prompt: "You are helpful."
steps:
  - id: step_1
    type: llm_call
    target_role: assistant
    parameters:
      prompt_prefix: "Please answer:"
    next: step_2
  - id: step_2
    type: assign
    parameters:
      target: result
      value: "$input"
```

### `roles` 正式 schema（Workflow 与 Role YAML 对齐）

```yaml
roles:
  - id: assistant
    name: Assistant
    system_prompt: "You are helpful."
    provider: openai
    model: gpt-4o-mini
    temperature: 0.2
    max_tokens: 512
    max_tool_rounds: 4
    max_history_messages: 80
    stream_buffer_capacity: 256
    event_modules: "llm_handler,tool_handler"
    event_routes: |
      event.type == ChatRequestEvent -> llm_handler
    connectors: [my_api, my_mcp]
    extensions:
      event_modules: "fallback_module"
      event_routes: "event.type == X -> fallback_module"
```

- `roles` 配置会透传到 `ConfigureRoleAgentEvent`，并在 `RoleGAgent` 运行时生效。
- `event_modules/event_routes` 合并优先级：平铺字段 > `extensions.*`。
- `workflow yaml roles` 与独立 `role yaml` 共享同一归一化语义，避免双套解析规则。

## 2. Data 原语

### `transform`

- 作用：对输入做纯文本变换（如 `trim`/`uppercase`/`count_words`/`split`）。
- 常用参数：`op`、`n`、`separator`。

```yaml
steps:
  - id: normalize_text
    type: transform
    parameters:
      op: trim
```

### `assign`

- 作用：给 workflow 变量赋值（运行时写入变量上下文）。
- 常用参数：`target`、`value`（可用 `$input`）。

```yaml
steps:
  - id: save_input
    type: assign
    parameters:
      target: user_question
      value: "$input"
```

### `retrieve_facts`

- 作用：按关键词从输入文本中检索最相关片段。
- 常用参数：`query`、`top_k`。

```yaml
steps:
  - id: extract_facts
    type: retrieve_facts
    parameters:
      query: "latency timeout error"
      top_k: "3"
```

### `cache`

- 作用：按 key 缓存子步骤结果，命中直接返回，未命中执行子步骤。
- 常用参数：`cache_key`、`ttl_seconds`、`child_step_type`、`child_target_role`。

```yaml
steps:
  - id: cached_answer
    type: cache
    parameters:
      cache_key: "$input"
      ttl_seconds: "600"
      child_step_type: "llm_call"
      child_target_role: "assistant"
```

## 3. Control 原语

### `guard`（别名：`assert`）

- 作用：输入校验门禁；失败可 `fail`、`skip` 或 `branch`。
- 常用参数：`check`、`on_fail`、`pattern`、`max`、`keyword`、`branch_target`。

```yaml
steps:
  - id: ensure_not_empty
    type: guard
    parameters:
      check: not_empty
      on_fail: fail
```

### `conditional`

- 作用：二分分支，输出分支 key（`true`/`false`）供引擎路由。
- 常用参数：`condition`。
- 注意：建议在 step 上配置 `branches.true` 与 `branches.false`。

```yaml
steps:
  - id: decide_path
    type: conditional
    parameters:
      condition: "urgent"
    branches:
      true: urgent_path
      false: normal_path
```

### `switch`

- 作用：多路分支匹配，命中分支后路由到目标步骤。
- 常用参数：`on`、`branch.{key}`（如 `branch.bug`）。
- 注意：建议同时配置 `parameters.branch.*` 和 `branches`，并提供 `_default`。

```yaml
steps:
  - id: route_issue
    type: switch
    parameters:
      on: "$input"
      branch.bug: bug_handler
      branch.feature: feature_handler
      branch._default: fallback_handler
    branches:
      bug: bug_handler
      feature: feature_handler
      _default: fallback_handler
```

### `while`（别名：`loop`）

- 作用：循环执行子步骤，直到条件不满足或达到最大迭代次数。
- 常用参数：`step`、`max_iterations`、`condition`、`sub_param_{key}`。

```yaml
steps:
  - id: refine_loop
    type: while
    target_role: writer
    parameters:
      step: llm_call
      max_iterations: "5"
      condition: "${lt(iteration, 5)}"
      sub_param_prompt_prefix: "Refine and improve:"
```

### `delay`（别名：`sleep`）

- 作用：暂停执行一段时间后继续。
- 常用参数：`duration_ms`。

```yaml
steps:
  - id: cool_down
    type: delay
    parameters:
      duration_ms: "1500"
```

### `wait_signal`（别名：`wait`）

- 作用：等待外部信号（可设置超时）。
- 常用参数：`signal_name`、`prompt`、`timeout_ms`。
- 运行时事件：`WaitingForSignalEvent` 会显式携带 `run_id + step_id + signal_name`，用于无状态 UI 回传。

```yaml
steps:
  - id: wait_for_approve
    type: wait_signal
    parameters:
      signal_name: "release_approved"
      prompt: "Waiting for release approval"
      timeout_ms: "60000"
```

### `checkpoint`

- 作用：写入检查点，便于恢复与审计。
- 常用参数：`name`。

```yaml
steps:
  - id: save_checkpoint
    type: checkpoint
    parameters:
      name: "before_publish"
```

## 4. AI 原语

### `llm_call`

- 作用：调用目标角色 LLM 完成推理或生成。
- 常用参数：`prompt_prefix`。

```yaml
roles:
  - id: analyst
    system_prompt: "You are a strict technical analyst."
steps:
  - id: analyze
    type: llm_call
    target_role: analyst
    parameters:
      prompt_prefix: "Analyze this input:"
```

### `tool_call`

- 作用：调用已注册工具（函数/工具链/MCP 工具）。
- 常用参数：`tool`。

```yaml
steps:
  - id: call_tool
    type: tool_call
    parameters:
      tool: "web_search"
```

### `evaluate`（别名：`judge`）

- 作用：LLM 评审打分，可按阈值分流。
- 常用参数：`criteria`、`scale`、`threshold`、`on_below`。

```yaml
steps:
  - id: score_answer
    type: evaluate
    target_role: reviewer
    parameters:
      criteria: "correctness and clarity"
      scale: "1-5"
      threshold: "4"
      on_below: "rewrite"
```

### `reflect`

- 作用：自我反思与改进循环，直到达标或达到轮数上限。
- 常用参数：`max_rounds`、`criteria`。

```yaml
steps:
  - id: self_reflect
    type: reflect
    target_role: writer
    parameters:
      max_rounds: "3"
      criteria: "accuracy and conciseness"
```

## 5. Composition 原语

### `foreach`（别名：`for_each`、`foreach_llm`）

- 作用：按分隔符拆分输入，对每个条目执行子步骤，再合并结果。
- 常用参数：`delimiter`、`sub_step_type`、`sub_target_role`、`sub_param_{key}`。
- Ergonomic 说明：`foreach_llm` 会在解析期归一化为 `foreach`，并在未显式指定时自动补 `sub_step_type=llm_call`。

```yaml
steps:
  - id: per_item_process
    type: foreach
    parameters:
      delimiter: "\n---\n"
      sub_step_type: "llm_call"
      sub_target_role: "assistant"
      sub_param_prompt_prefix: "Process item:"
```

### `parallel`（别名：`parallel_fanout`、`fan_out`）

- 作用：并行扇出到多个 worker，收敛合并，可选接投票步骤。
- 常用参数：`workers`、`parallel_count`、`vote_step_type`、`vote_param_{key}`。

```yaml
steps:
  - id: fanout_analyze
    type: parallel
    parameters:
      workers: "agent_a,agent_b,agent_c"
      vote_step_type: "vote_consensus"
```

### `race`（别名：`select`）

- 作用：并行发送到多个 worker，返回最先完成的结果。
- 常用参数：`workers`、`count`。

```yaml
steps:
  - id: first_answer_wins
    type: race
    parameters:
      workers: "fast_model,cheap_model"
      count: "2"
```

### `map_reduce`（别名：`mapreduce`、`map_reduce_llm`）

- 作用：先 map（分片并行处理），再 reduce（汇总归并）。
- 常用参数：`delimiter`、`map_step_type`、`map_target_role`、`reduce_step_type`、`reduce_target_role`、`reduce_prompt_prefix`。
- Ergonomic 说明：`map_reduce_llm` 会在解析期归一化为 `map_reduce`，并在未显式指定时自动补 `map_step_type=llm_call`、`reduce_step_type=llm_call`。

```yaml
steps:
  - id: summarize_chunks
    type: map_reduce
    parameters:
      delimiter: "\n---\n"
      map_step_type: "llm_call"
      map_target_role: "mapper"
      reduce_step_type: "llm_call"
      reduce_target_role: "reducer"
      reduce_prompt_prefix: "Merge these chunk summaries:"
```

### `workflow_call`（别名：`sub_workflow`）

- 作用：调用子工作流，并将子工作流完成态返回到当前步骤。
- 常用参数：`workflow`、`lifecycle`。
- `lifecycle` 语义：
  - `singleton`（默认）：复用同名子工作流 actor；
  - `transient`：每次调用独立 actor，子流程完成后销毁；
  - `scope`：与 `transient` 相同生命周期策略（保留语义别名，便于上层配置表达）。
- `lifecycle` 校验：
  - 仅允许 `singleton/transient/scope`；
  - 非法值会在校验阶段或模块执行阶段直接失败，不再回落到默认值。
- 运行时关联语义：
  - `workflow_call` 调用会生成统一格式的 invocation id：`<parent_run_id>:workflow_call:<parent_step_id|step>:<guidN>`；
  - 该规则由共享工厂统一生成，模块层与 actor 编排层保持一致；
  - 子流程 `child_run_id` 复用 invocation id，便于跨事件链路关联与回放定位。

```yaml
steps:
  - id: call_sub_workflow
    type: workflow_call
    parameters:
      workflow: "shared_enrichment_pipeline"
      lifecycle: "singleton"
```

### `dynamic_workflow`

- 作用：从上一步输出中提取 YAML 代码块，动态重配当前 workflow actor 后继续执行。
- 常用参数：`original_input`（可选，作为动态流程启动输入）。
- 说明：仅在非 `closed_world_mode` 下可用；若输入中无 YAML 代码块则返回失败 `StepCompletedEvent`。

```yaml
steps:
  - id: apply_generated_workflow
    type: dynamic_workflow
    parameters:
      original_input: "{{user_request}}"
```

### `vote_consensus`（别名：`vote`）

- 作用：对候选结果做共识选择，常和 `parallel` 组合使用。
- 常用参数：无。

```yaml
steps:
  - id: consensus
    type: vote_consensus
```

## 6. Integration 原语

### `connector_call`（别名：`bridge_call`、`cli_call`、`mcp_call`、`http_get`、`http_post`、`http_put`、`http_delete`）

- 作用：调用外部 connector（HTTP/CLI/MCP 等），支持重试和降级策略。
- 常用参数：`connector`、`operation`、`retry`、`timeout_ms`、`optional`、`on_missing`、`on_error`。
- Ergonomic 说明（统一归一化到 `connector_call`）：
  - `http_get`/`http_post`/`http_put`/`http_delete`：自动补 `method=GET/POST/PUT/DELETE`（若未显式提供）。
  - `mcp_call`：若只写 `tool` 且未写 `operation/action`，会自动补 `operation=<tool>`。
  - `cli_call`：仅语义别名，不改变执行语义。

```yaml
steps:
  - id: call_external
    type: connector_call
    target_role: coordinator
    parameters:
      connector: "incident_api"
      operation: "create_ticket"
      retry: "2"
      timeout_ms: "10000"
      on_error: "continue"
```

```yaml
steps:
  - id: get_health
    type: http_get
    target_role: coordinator
    parameters:
      connector: "internal_http"
      path: "/healthz"
```

### `emit`（别名：`publish`）

- 作用：向外发布事件，用于通知或集成事件驱动链路。
- 常用参数：`event_type`、`payload`。

```yaml
steps:
  - id: publish_event
    type: emit
    parameters:
      event_type: "workflow.completed"
      payload: "$input"
```

## 7. Human 原语

### `human_input`

- 作用：暂停并等待人工输入。
- 常用参数：`prompt`、`variable`、`timeout`、`on_timeout`。

```yaml
steps:
  - id: ask_human
    type: human_input
    parameters:
      prompt: "Please provide customer decision:"
      variable: "review_decision"
      timeout: "1800"
      on_timeout: "fail"
```

### `human_approval`

- 作用：暂停并等待人工批准/拒绝。
- 常用参数：`prompt`、`timeout`、`on_reject`。

```yaml
steps:
  - id: approval_gate
    type: human_approval
    parameters:
      prompt: "Approve release?"
      timeout: "3600"
      on_reject: "fail"
```

### 实际应用集成模式（`human_input` / `human_approval` / `wait_signal`）

推荐把“人工/外部系统回调”当作**标准双向事件交互**来接入：

1. Workflow 运行到阻塞点，发出等待事件（SSE/WebSocket/EventBus 都可）。
2. App 渲染交互 UI（输入框、审批按钮、发送信号表单）。
3. App 收集用户/系统回调后，回发 resume/signal 事件给同一个 run（显式携带 `actorId + runId`）。

事件对照：

- `human_input` / `human_approval`：`WorkflowSuspendedEvent` -> `WorkflowResumedEvent`
- `wait_signal`：`WaitingForSignalEvent(run_id, step_id, signal_name, ...)` -> `SignalReceivedEvent`

建议的请求契约（以 Web API 为例）：

```json
POST /api/workflows/resume
{
  "actorId": "wf-2f3f...",
  "runId": "c7e0...",
  "stepId": "approval_gate",
  "approved": true,
  "userInput": "approved by oncall",
  "metadata": { "operator": "alice" }
}
```

```json
POST /api/workflows/signal
{
  "actorId": "wf-2f3f...",
  "runId": "c7e0...",
  "signalName": "ops_window_open",
  "payload": "window=2026-02-25T21:00Z"
}
```

约定与注意事项：

- `actorId`：必须来自当前运行上下文（例如 `RUN_STARTED` 或 `workflow.suspended` / `workflow.waiting_signal` 事件）。
- `runId`：必须来自当前运行上下文（优先使用 `workflow.waiting_signal` 或 `workflow.suspended` 事件中显式携带的 runId）。
- `stepId`：resume 时必须对应当前挂起步骤；不要用旧步骤 ID 复用请求。
- `signalName`：建议统一小写蛇形命名，和 YAML `signal_name` 保持一致。
- 交互端点为无状态契约：服务端不维护 `runId -> actorId` 进程内映射，调用方必须在每次请求里显式传入 `actorId` 与 `runId`。
- `human_approval.on_reject`：
  - `fail`：拒绝会终止流程；
  - `skip`：拒绝后继续下一个步骤（输入保持原值）。
- `wait_signal.timeout_ms`：超时会返回失败 `StepCompletedEvent`，上层可配 `on_error` 做降级。
- UI 层建议把“待处理交互卡片”与执行日志放在一起，便于审计 run 的人工干预轨迹。

## 8. 引擎内部原语

### `workflow_loop`

- 作用：工作流主循环调度器（派发步骤、接收 `StepCompletedEvent`、推进到下一步/结束）。
- 常用参数：无（由引擎注入）。
- 使用方式：**不建议在 YAML 中手写**，由依赖推导器自动装配。

```yaml
# internal-only: runtime injects this module automatically
# type: workflow_loop
```

## 9. 闭世界图灵完备实践建议

在 `closed_world_mode: true` 下，建议优先组合以下原语做确定性编排：

- 状态写入：`assign`
- 条件跳转：`conditional` / `switch`
- 循环推进：`while`（或通过分支回边实现循环）
- 表达式计算：在参数里使用 `${add/sub/eq/lt/...}`

可参考示例：

- `workflows/turing-completeness/counter-addition.yaml`
- `workflows/turing-completeness/minsky-inc-dec-jz.yaml`

