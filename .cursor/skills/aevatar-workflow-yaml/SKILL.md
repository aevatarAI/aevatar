---
name: aevatar-workflow-yaml
description: Write and edit Aevatar workflow YAML definitions. Covers canonical schema, closed_world_mode, formal roles config (provider/model/limits/event modules/routes/connectors), all 26 primitives, branching, retry/error policies, and validation constraints.
---

# Aevatar Workflow YAML Authoring

Use this skill when creating, editing, reviewing, or debugging `workflow yaml` files.

## Canonical Schema

All keys use snake_case (`UnderscoredNamingConvention`).
The only supported top-level fields are {{workflow_authorable_root_fields}}.
Do not emit top-level fields from other workflow dialects, including {{workflow_unsupported_dialect_root_fields}}.

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
    model: gpt-5.4          # optional
    temperature: 0.2            # optional
    max_tokens: 512             # optional
    max_tool_rounds: 4          # optional
    max_history_messages: 50    # optional
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
12. Do not use direct GAgent dispatch hints such as `parameters.agent_type` or `parameters.agent_id` in Studio-authored workflow YAML. Model executable identity through roles and `target_role`.

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
    model: gpt-5.4
    temperature: 0.2
    max_tokens: 512
    max_tool_rounds: 4
    max_history_messages: 50
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

### External Messaging via NyxID Relay

Workflow-local Telegram bridge actors are retired. When a workflow is triggered from a channel message, keep the channel traffic on the NyxID relay path: NyxID forwards the inbound platform message to Aevatar's `/api/webhooks/nyxid-relay` callback, the workflow processes normalized relay context, and replies go back through NyxID channel relay APIs instead of a workflow-owned send/wait-reply actor.

```yaml
steps:
  - id: compose_relay_reply
    type: llm_call
    role: advisor
    parameters:
      prompt_prefix: |
        Please answer the inbound channel request.
        Message: ${relay.message.text}
    next: send_relay_reply

  - id: send_relay_reply
    type: connector_call
    parameters:
      connector: nyxid_channel_relay
      operation: /api/v1/channel-relay/reply
      message_id: "${relay.message_id}"
      text: "${compose_relay_reply}"
      timeout_ms: "30000"
```

If the work needs an external agent such as OpenClaw, model that as a normal relay conversation owned by NyxID and resume the workflow from the next inbound relay callback or a persisted continuation. Do not add workflow-local polling steps for platform chat history.

```yaml
steps:
  - id: request_external_research
    type: connector_call
    parameters:
      connector: nyxid_channel_relay
      operation: /api/v1/channel-relay/reply
      message_id: "${relay.message_id}"
      text: |
        @${relay.external_agent_username}
        Please research this repository and summarize the architecture.
        Repo URL: ${collect_repo_url}
        Please include final architecture details in your reply.
      timeout_ms: "30000"
    next: mark_external_research_pending

  - id: mark_external_research_pending
    type: assign
    parameters:
      target: "external_research_status"
      value: "pending_relay_callback"

  - id: process_openclaw_result
    type: assign
    parameters:
      target: "architecture_summary"
      value: "${relay.message.text}"  # Supplied by the next inbound NyxID relay callback

  - id: timeout_fallback
    type: assign
    parameters:
      target: relay_continuation_timeout
      value: "Relay continuation timeout"
```

**Key Points for Relay Delegation:**
1. **Inbound ownership**: NyxID owns the platform webhook and forwards normalized channel messages to Aevatar.
2. **Outbound ownership**: Aevatar sends replies through NyxID channel relay APIs, usually `/api/v1/channel-relay/reply`.
3. **Continuation**: Long-running external work should resume from a later relay callback or persisted workflow continuation.
4. **No workflow polling**: Do not poll platform chat history or wait for replies inside a workflow step.

### Prompt Composition for External Agents

When a workflow asks an external agent to continue work through the relay, prompt quality matters more than strict format contracts.

Use this structure:

1. **Who + objective** (one short line)
2. **Concrete task list** (3-6 numbered items)
3. **Resolved runtime parameters** (single final values only)
4. **Minimal output hint** (soft preference, not hard protocol)

Key rules:

- Resolve workflow decisions first, then send only final facts.
  - Good: `report_output_directory: /Users/me/Report`
  - Bad: `if user says yes then use path A else path B`
- Do not forward raw control signals (`yes`, `no`, `true`, `false`) without context.
  - Convert them into explicit business meaning before sending.
- Prefer soft wording for external agents:
  - `please include ... if possible` / `尽量包含`
  - Avoid brittle `must return exact JSON` unless the target is known to obey it.
- Keep bridge prompts short and actionable; avoid policy/debug text irrelevant to the target.
- When sending user-provided paths/URLs (e.g. `~/Report`, `REPORT_PATH`), prefer plain text transport.
  - Avoid `parse_mode: Markdown` unless you fully escape Markdown symbols.
  - Otherwise `~`, `_`, `*`, `[]`, `()` may alter visible text.

Anti-pattern (bad):

```yaml
prompt_prefix: |
  Default dir: ~/Report
  Human decision: ${collect_report_directory_decision}
  If human says yes:/path then use that path else default.
```

Better (good):

```yaml
steps:
  - id: route_report_directory
    type: conditional
    parameters:
      condition: "/"
    branches:
      true: set_custom_report_directory
      false: set_default_report_directory

  - id: set_default_report_directory
    type: assign
    parameters:
      target: "report_output_directory"
      value: "~/Report"

  - id: set_custom_report_directory
    type: assign
    parameters:
      target: "report_output_directory"
      value: "$input"

  - id: send_to_openclaw
    type: connector_call
    parameters:
      connector: nyxid_channel_relay
      operation: /api/v1/channel-relay/reply
      message_id: "${relay.message_id}"
      text: |
        @${relay.external_agent_username}
        Please research this repository and write a report.
        Repo URL: ${collect_repo_url}
        Report output directory: ${report_output_directory}
        Please include final REPORT_PATH if possible.
```

### Runtime Defaults From config.json

You can inject shared runtime values via `WorkflowRuntimeDefaults` in host `config.json`; they become run metadata variables and can be referenced as `${...}` in workflow YAML. Channel callback payload such as `relay.message_id` and `relay.message.text` comes from the NyxID relay ingress rather than static defaults.

```json
{
  "WorkflowRuntimeDefaults": {
    "relay.external_agent_username": "openclaw_bot"
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
