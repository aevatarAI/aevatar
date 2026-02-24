---
name: aevatar-workflow-yaml
description: Write and edit Aevatar workflow YAML definitions. Covers schema, all 26 primitives (transform, guard, llm_call, parallel, etc.), parameters, roles, steps, branching, retry/error policies. Use when creating, editing, or reviewing workflow YAML files, or when the user asks about workflow syntax, primitives, or step parameters.
---

# Aevatar Workflow YAML Authoring

## Schema Overview

All keys use **snake_case** (`UnderscoredNamingConvention`).

```yaml
name: my_workflow            # required — unique workflow identifier
description: |               # optional — human-readable description
  What this workflow does.
roles:                       # optional — LLM persona definitions
  - id: analyst              # required (or use `name:`)
    name: Analyst             # required (or use `id:`)
    system_prompt: |          # optional — LLM system message
      You are a systems analyst.
    provider: openai          # optional — LLM provider
    model: gpt-4              # optional — model name
    event_modules: "mod1,mod2" # optional — comma-separated module list
    connectors:               # optional — allowed connector names
      - api_connector
steps:                       # optional — ordered step definitions
  - id: step1                # required — unique step identifier
    type: llm_call            # optional — primitive type (default: "llm_call")
    target_role: analyst      # optional — role to execute this step (alias: role)
    parameters:               # optional — Dict<string, string>, module-specific
      prompt_prefix: "Analyze:"
    next: step2               # optional — explicit next step ID
    children: []              # optional — nested sub-steps (recursive)
    branches:                 # optional — Dict<string, string> (key → step_id)
      bug: handle_bug
      _default: fallback      # "_default" = fallback branch
    retry:                    # optional — retry policy
      max_attempts: 3         #   1–10, default 3
      backoff: exponential    #   "fixed" | "exponential", default "fixed"
      delay_ms: 1000          #   0–60000, default 1000
    on_error:                 # optional — error handling
      strategy: fail          #   "fail" | "skip" | "fallback", default "fail"
      fallback_step: step_x   #   required when strategy=fallback
      default_output: ""      #   used when strategy=skip
    timeout_ms: 30000         # optional — step timeout in ms
```

## Key Rules

1. **`type` defaults to `"llm_call"`** when omitted.
2. **`target_role` and `role`** are aliases; `target_role` takes precedence.
3. **Role `id` and `name`** are interchangeable — if one is missing, the other is used as both.
4. **`parameters`** is always `Dict<string, string>` — all values are strings, even numbers.
5. **Step flow**: `next` → explicit jump; `branches` → conditional routing; neither → falls through to next step in list order.
6. **`children`** is recursive — supports any nesting depth (used by `parallel`, `foreach`, `while`).
7. **`_default`** is the reserved branch key for switch/conditional fallback.
8. **Dynamic parameter prefixes**: some modules read `branch.{key}`, `sub_param_{key}`, `vote_param_{key}`.

## Step Types (26 Primitives)

| Category    | Type              | Aliases                           | Purpose                                       |
|-------------|-------------------|-----------------------------------|-----------------------------------------------|
| data        | `transform`       | transform                         | Pure text ops (uppercase, count, split, etc.)  |
| data        | `assign`          | assign                            | Set a workflow variable                        |
| data        | `retrieve_facts`  | retrieve_facts                    | Keyword search over input lines                |
| data        | `cache`           | cache                             | Cache child step results by key                |
| control     | `guard`           | guard, assert                     | Input validation gate                          |
| control     | `conditional`     | conditional                       | Binary branch (keyword contains)               |
| control     | `switch`          | switch                            | Multi-way branch                               |
| control     | `while`           | while, loop                       | Repeat sub-step N times                        |
| control     | `delay`           | delay, sleep                      | Pause execution                                |
| control     | `wait_signal`     | wait_signal, wait                 | Block until external signal                    |
| control     | `checkpoint`      | checkpoint                        | Save workflow state                            |
| ai          | `llm_call`        | llm_call                          | Send prompt to LLM role                        |
| ai          | `tool_call`       | tool_call                         | Invoke a registered tool                       |
| ai          | `evaluate`        | evaluate, judge                   | LLM-as-judge scoring                           |
| ai          | `reflect`         | reflect                           | Self-critique & improve loop                   |
| composition | `foreach`         | foreach, for_each                 | Iterate over delimited items                   |
| composition | `parallel`        | parallel_fanout, parallel, fan_out| Fan-out to multiple workers                    |
| composition | `race`            | race, select                      | First-response-wins parallel                   |
| composition | `map_reduce`      | map_reduce, mapreduce             | Split → map → reduce                           |
| composition | `workflow_call`   | workflow_call, sub_workflow        | Call another workflow                           |
| composition | `vote_consensus`  | vote_consensus, vote              | Aggregate votes for consensus                  |
| integration | `connector_call`  | connector_call, bridge_call       | Call external connector                        |
| integration | `emit`            | emit, publish                     | Publish event externally                       |
| human       | `human_input`     | human_input                       | Wait for human freeform input                  |
| human       | `human_approval`  | human_approval                    | Wait for human approve/reject                  |

## Common Patterns

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

> When no `next` is specified, steps execute in list order.

### LLM Chain (Multi-role)

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

### Multi-way Branching (switch)

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
  - id: handle_bug
    type: transform
    parameters: { op: "uppercase" }
    next: done
  - id: handle_feature
    type: transform
    parameters: { op: "lowercase" }
    next: done
  - id: handle_other
    type: transform
    parameters: { op: "trim" }
    next: done
  - id: done
    type: assign
    parameters: { target: "result", value: "$input" }
```

> Both `parameters.branch.*` and `branches` must be set for switch.
> Each branch target should `next: done` to converge.

### Parallel Fan-out

```yaml
roles:
  - id: worker_a
    system_prompt: "You are an engineer."
  - id: worker_b
    system_prompt: "You are a strategist."
steps:
  - id: brainstorm
    type: parallel
    parameters:
      workers: "worker_a,worker_b"
```

### Map-Reduce

```yaml
steps:
  - id: analyze
    type: map_reduce
    parameters:
      delimiter: "\n---\n"
      map_step_type: "llm_call"
      map_target_role: "mapper"
      reduce_step_type: "llm_call"
      reduce_target_role: "reducer"
```

### Retry & Error Handling

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

## Parameter Reference

For complete per-module parameter documentation (all keys, defaults, allowed values), see [parameters.md](parameters.md).

## Full Examples

For complete working YAML examples covering each primitive, see [examples.md](examples.md).
