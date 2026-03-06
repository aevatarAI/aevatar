---
name: aevatar-workflow-yaml
description: Write and edit Aevatar workflow YAML definitions. Covers canonical schema, closed_world_mode, formal roles config (provider/model/limits/event modules/routes/connectors), all 26 primitives, branching, retry/error policies, and validation constraints.
---

# Aevatar Workflow YAML Authoring

Use this skill when creating, editing, reviewing, or debugging `workflow yaml` files.

## Canonical Schema

All keys use snake_case (`UnderscoredNamingConvention`).

```yaml
name: my_workflow               # required
description: |                  # optional
  What this workflow does.

configuration:                  # optional
  closed_world_mode: false      # optional, default false

roles:                          # optional - formal RoleGAgent config
  - id: analyst                 # required (or use name)
    name: Analyst               # required (or use id)
    system_prompt: |            # optional
      You are a systems analyst.
    provider: openai            # optional
    model: gpt-4o-mini          # optional
    temperature: 0.2            # optional
    max_tokens: 512             # optional
    max_tool_rounds: 4          # optional
    max_history_messages: 50    # optional
    stream_buffer_capacity: 128 # optional
    event_modules: "mod1,mod2"  # optional, comma-separated
    event_routes: |             # optional, DSL or YAML list
      event.type == ChatRequestEvent -> mod1
    connectors:                 # optional
      - api_connector
    extensions:                 # optional compatibility container
      event_modules: "legacy_mod"
      event_routes: "event.type == LegacyEvent -> legacy_mod"

steps:                          # required in practice
  - id: step1                   # required, unique
    type: llm_call              # optional, default "llm_call"
    target_role: analyst        # optional, alias: role
    parameters:                 # optional, Dict<string,string>
      prompt_prefix: "Analyze:"
      agent_type: TelegramBridgeGAgent  # optional: direct GAgent type dispatch (llm/evaluate/reflect)
      agent_id: bridge:telegram:default # optional: explicit target actor id
    next: step2                 # optional
    children: []                # optional, recursive
    branches:                   # optional, Dict<string,string>
      true: next_a
      false: next_b
      _default: fallback
    retry:                      # optional
      max_attempts: 3           # default 3
      backoff: exponential      # fixed | exponential, default fixed
      delay_ms: 1000            # default 1000
    on_error:                   # optional
      strategy: fail            # fail | skip | fallback
      fallback_step: step_x
      default_output: ""
    timeout_ms: 30000           # optional
```

## Critical Rules

1. `type` defaults to `llm_call`.
2. `target_role` and `role` are aliases; `target_role` wins.
3. Role `id` and `name` fallback: if one is missing, the other is used for both.
4. `parameters` is `Dict<string,string>`; use string values in authoring.
5. Step flow precedence: branch routing -> `next` -> list-order fallback.
6. `children` is recursive and can nest arbitrarily.
7. `_default` is the reserved fallback branch key.
8. Dynamic parameter keys are used by some modules, e.g. `branch.{key}`, `sub_param_{key}`, `vote_param_{key}`.
9. Workflow roles and standalone role YAML share the same normalization semantics.
10. `event_modules` / `event_routes` precedence: top-level fields > `extensions.*`.
11. Ergonomic aliases are normalized at parse-time to canonical primitives:
   - `http_get/http_post/http_put/http_delete/mcp_call/cli_call` -> `connector_call`
   - `foreach_llm` -> `foreach`
   - `map_reduce_llm` -> `map_reduce`
12. `parameters.agent_type` is supported for `llm_call` / `evaluate` / `reflect` and can directly target a GAgent type.
13. When `parameters.agent_type` is present, `target_role` can be omitted and is not required for target resolution.
14. `parameters.agent_id` is optional; if omitted, runtime generates a stable actor id from workflow actor + step + agent type.
15. In agent-type dispatch mode, step `parameters` are forwarded as chat metadata except `agent_type` and `agent_id`.

## Validation Constraints

- `conditional` should define both `branches.true` and `branches.false`.
- `switch` should define `_default` in `branches`.
- `while` should provide at least one of:
  - `condition`
  - positive `max_iterations`
- `workflow_call` should include `parameters.workflow`.
- Step IDs should be unique.

### Closed World Mode

When `configuration.closed_world_mode: true`, the following step types are blocked:

- `llm_call`
- `tool_call`
- `connector_call` / `bridge_call`
- `http_get` / `http_post` / `http_put` / `http_delete` / `mcp_call` / `cli_call`
- `evaluate` / `judge`
- `reflect`
- `human_input`
- `human_approval`
- `wait_signal` / `wait`
- `emit` / `publish`
- `parallel` / `parallel_fanout` / `fan_out`
- `race` / `select`
- `map_reduce` / `mapreduce`
- `map_reduce_llm`
- `vote_consensus` / `vote`
- `foreach` / `for_each` / `foreach_llm`

## Primitive Catalog (26 Total)

| Category | Type | Aliases | Purpose |
|---|---|---|---|
| data | `transform` | transform | Pure text ops (uppercase, count, split, etc.) |
| data | `assign` | assign | Set a workflow variable |
| data | `retrieve_facts` | retrieve_facts | Keyword search over input lines |
| data | `cache` | cache | Cache child step results by key |
| control | `guard` | guard, assert | Input validation gate |
| control | `conditional` | conditional | Binary branching |
| control | `switch` | switch | Multi-way branching |
| control | `while` | while, loop | Repetition loop |
| control | `delay` | delay, sleep | Pause execution |
| control | `wait_signal` | wait_signal, wait | Wait for external signal |
| control | `checkpoint` | checkpoint | Save execution point |
| ai | `llm_call` | llm_call | Send prompt to role LLM |
| ai | `tool_call` | tool_call | Invoke registered tool |
| ai | `evaluate` | evaluate, judge | LLM-as-judge scoring |
| ai | `reflect` | reflect | Self-critique and improve |
| composition | `foreach` | foreach, for_each, foreach_llm | Iterate by delimiter |
| composition | `parallel` | parallel_fanout, parallel, fan_out | Fan-out to multiple workers |
| composition | `race` | race, select | First-response-wins |
| composition | `map_reduce` | map_reduce, mapreduce, map_reduce_llm | Split -> map -> reduce |
| composition | `workflow_call` | workflow_call, sub_workflow | Invoke sub-workflow |
| composition | `vote_consensus` | vote_consensus, vote | Consensus aggregation |
| integration | `connector_call` | connector_call, bridge_call, cli_call, mcp_call, http_get, http_post, http_put, http_delete | Call external connector |
| integration | `emit` | emit, publish | Publish external event |
| human | `human_input` | human_input | Wait for human text input |
| human | `human_approval` | human_approval | Wait for human approval |
| internal | `workflow_loop` | workflow_loop | Runtime orchestrator (do not hand-author in normal YAML) |

## Common Patterns

### Role Formalization (Full Role Config)

```yaml
configuration:
  closed_world_mode: false
roles:
  - id: planner
    name: Planner
    system_prompt: "You plan robust workflows."
    provider: openai
    model: gpt-4o-mini
    temperature: 0.2
    max_tokens: 512
    max_tool_rounds: 4
    max_history_messages: 50
    stream_buffer_capacity: 128
    event_modules: "llm_handler,tool_handler"
    event_routes: |
      event.type == ChatRequestEvent -> llm_handler
    connectors: [search_api, issue_tracker]
    extensions:
      event_modules: "legacy_module"
      event_routes: "event.type == LegacyEvent -> legacy_module"
```

In this example, runtime uses top-level `event_modules/event_routes` rather than `extensions.*`.

### Linear Pipeline

```yaml
steps:
  - id: validate
    type: guard
    parameters: { check: "not_empty" }
    next: process
  - id: process
    type: transform
    parameters: { op: "uppercase" }
    next: output
  - id: output
    type: assign
    parameters: { target: "result", value: "$input" }
```

When no `next` is specified, list order is used.

### Multi-role LLM Chain

```yaml
roles:
  - id: analyst
    system_prompt: "Identify the top 3 problems."
  - id: advisor
    system_prompt: "Propose solutions for each problem."
steps:
  - id: analyze
    type: llm_call
    role: analyst
    next: propose
  - id: propose
    type: llm_call
    role: advisor
```

### Direct GAgent Type Dispatch (No YAML role)

Use when a step should call a concrete GAgent directly:

```yaml
steps:
  - id: send_to_telegram_bridge
    type: llm_call
    parameters:
      agent_type: TelegramBridgeGAgent
      agent_id: bridge:telegram:openclaw
      connector: telegram
      operation: /sendMessage
      chat_id: "${telegram.chat_id}"
      parse_mode: Markdown
```

The same `agent_type` pattern also works for `evaluate` and `reflect`.

### Telegram Group Stream Pattern (No callback)

```yaml
steps:
  - id: send_to_openclaw
    type: llm_call
    parameters:
      agent_type: TelegramBridgeGAgent
      connector: telegram
      chat_id: "${telegram.chat_id}"
      prompt_prefix: |
        [AEVATAR -> OPENCLAW]
        Reply in the same group and prefix response with [AEVATAR_STREAM_REPLY].
    next: wait_openclaw_group_stream

  - id: wait_openclaw_group_stream
    type: llm_call
    parameters:
      agent_type: TelegramBridgeGAgent
      connector: telegram
      operation: /waitReply
      chat_id: "${telegram.chat_id}"
      expected_from_username: "${telegram.openclaw_bot_username}"
      correlation_contains: "[AEVATAR_STREAM_REPLY]"
      wait_timeout_ms: "120000"
      poll_timeout_sec: "8"
      start_from_latest: "true"
    on_error:
      strategy: fallback
      fallback_step: timeout_fallback

  - id: timeout_fallback
    type: assign
    parameters:
      target: bridge_timeout
      value: "OpenClaw reply timeout"
```

Recommended runtime flow:
- Aevatar bot sends message to Telegram group via `/sendMessage`.
- OpenClaw bot replies in the same group with a correlation marker.
- Aevatar bot waits through `/waitReply` (internally polling `getUpdates`) and captures matched reply.

### Runtime Defaults From config.json

You can inject shared runtime values via `WorkflowRuntimeDefaults` in host `config.json`; they become run metadata variables and can be referenced as `${...}` in workflow YAML.

```json
{
  "WorkflowRuntimeDefaults": {
    "telegram.chat_id": "-1001234567890",
    "telegram.openclaw_bot_username": "openclaw_bot"
  }
}
```

Request metadata with the same key overrides configured defaults.

### Switch Branching

```yaml
steps:
  - id: route
    type: switch
    parameters:
      branch.bug: handle_bug
      branch.feature: handle_feature
      branch._default: handle_other
    branches:
      bug: handle_bug
      feature: handle_feature
      _default: handle_other
```

Both `parameters.branch.*` and `branches` are expected.

### Closed-world Deterministic Loop

```yaml
configuration:
  closed_world_mode: true
steps:
  - id: init
    type: assign
    parameters: { target: "i", value: "0" }
    next: loop
  - id: loop
    type: while
    parameters:
      condition: "${lt(i, 5)}"
      step: assign
      sub_param_target: "i"
      sub_param_value: "${add(i, 1)}"
```

### Retry and Error Handling

```yaml
steps:
  - id: risky_step
    type: connector_call
    parameters:
      connector: "external_api"
      timeout_ms: "10000"
    retry:
      max_attempts: 3
      backoff: exponential
      delay_ms: 2000
    on_error:
      strategy: fallback
      fallback_step: safe_default
```

### Connector Ergonomic Aliases

```yaml
steps:
  - id: read_health
    type: http_get
    parameters:
      connector: "internal_http"
      path: "/healthz"

  - id: run_cli
    type: cli_call
    parameters:
      connector: "demo_cli_dotnet"

  - id: invoke_mcp
    type: mcp_call
    parameters:
      connector: "demo_mcp"
      tool: "list_tools"
```

## References

- For full per-module parameters: [parameters.md](parameters.md)
- For full workflow samples: [examples.md](examples.md)
