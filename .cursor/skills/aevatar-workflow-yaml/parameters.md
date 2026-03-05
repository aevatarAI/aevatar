# Primitive Parameter Reference

All parameter values are strings. Defaults apply when the key is absent or empty.

---

## Data Primitives

### transform

| Parameter   | Description                              | Default    | Values |
|-------------|------------------------------------------|------------|--------|
| `op`        | Operation to apply                       | `identity` | `identity`, `uppercase`, `lowercase`, `trim`, `count`, `count_words`, `take`, `take_last`, `join`, `split`, `distinct`, `reverse_lines` |
| `n`         | Line count for `take`/`take_last`        | `5`        | integer |
| `separator` | Delimiter for `join`/`split`             | `\n`       | string |

### assign

| Parameter | Description                                       | Default | Values |
|-----------|---------------------------------------------------|---------|--------|
| `target`  | Variable name to assign to                        | `""`    | string |
| `value`   | Value to assign; `"$input"` captures current input| `""`    | string |

### retrieve_facts

| Parameter | Description                               | Default | Values |
|-----------|-------------------------------------------|---------|--------|
| `query`   | Keywords to search for in input lines     | `""`    | string |
| `top_k`   | Number of top results to return           | `5`     | integer |

### cache

| Parameter         | Description                            | Default    | Values |
|-------------------|----------------------------------------|------------|--------|
| `cache_key`       | Cache key (defaults to step input)     | input text | string |
| `ttl_seconds`     | TTL in seconds (1–86400)               | `3600`     | integer |
| `child_step_type` | Step type to execute on miss           | `llm_call` | any step type |
| `child_target_role`| Target role for the child step        | step's `target_role` | role id |

---

## Control Primitives

### guard

| Parameter       | Description                                | Default     | Values |
|-----------------|--------------------------------------------|-------------|--------|
| `check`         | Validation check to perform                | `not_empty` | `not_empty`, `json_valid`, `regex`, `max_length`, `contains` |
| `on_fail`       | Action when check fails                    | `fail`      | `fail`, `skip`, `branch` |
| `pattern`       | Regex (required when `check=regex`)        | —           | regex string |
| `max`           | Max length (required when `check=max_length`)| —         | integer |
| `keyword`       | Substring (required when `check=contains`) | —           | string |
| `branch_target` | Step ID (required when `on_fail=branch`)   | —           | step id |

### conditional

| Parameter   | Description                                          | Default   | Values |
|-------------|------------------------------------------------------|-----------|--------|
| `condition` | Keyword to search for in input (case-insensitive)    | `default` | string |

Sets `metadata["branch"]` to `"true"` or `"false"`.

### switch

| Parameter       | Description                                      | Default | Values |
|-----------------|--------------------------------------------------|---------|--------|
| `on`            | Value to match against (defaults to step input)  | input   | string |
| `branch.{key}`  | Maps a match key to a target step ID            | —       | `branch.bug: handle_bug` |

Also requires `branches:` in the step definition for routing.

### while

| Parameter        | Description                          | Default    | Values |
|------------------|--------------------------------------|------------|--------|
| `max_iterations` | Maximum loop iterations              | `10`       | integer |
| `step`           | Sub-step type to execute each round  | `llm_call` | any step type |

### delay

| Parameter     | Description                        | Default | Values |
|---------------|------------------------------------|---------|--------|
| `duration_ms` | Pause duration in ms (0–300000)    | `1000`  | integer |

### wait_signal

| Parameter    | Description                                    | Default   | Values |
|--------------|------------------------------------------------|-----------|--------|
| `signal_name`| Name of the signal to wait for                 | `default` | string |
| `prompt`     | Message to display while waiting               | `""`      | string |
| `timeout_ms` | Timeout in ms (0 = no timeout, max 3600000)    | `0`       | integer |

### checkpoint

| Parameter | Description                    | Default  | Values |
|-----------|--------------------------------|----------|--------|
| `name`    | Checkpoint label               | step id  | string |

---

## AI Primitives

### llm_call

| Parameter       | Description                             | Default | Values |
|-----------------|-----------------------------------------|---------|--------|
| `prompt_prefix` | Text prepended to input before LLM call | `""`    | string |

Requires `target_role` (or `role`) pointing to a role with `system_prompt`.

### tool_call

| Parameter | Description                     | Default | Values |
|-----------|---------------------------------|---------|--------|
| `tool`    | Name of the tool to invoke      | —       | string (required) |

### evaluate

| Parameter   | Description                              | Default   | Values |
|-------------|------------------------------------------|-----------|--------|
| `criteria`  | What to evaluate                         | `quality` | string |
| `scale`     | Numeric scale for scoring                | `1-5`     | string |
| `threshold` | Minimum passing score                    | `3`       | number as string |
| `on_below`  | Branch key when score < threshold        | `""`      | string |

Requires a judge role that returns a single numeric score.

### reflect

| Parameter    | Description                            | Default                    | Values |
|--------------|----------------------------------------|----------------------------|--------|
| `max_rounds` | Critique-improve cycles (1–10)         | `3`                        | integer |
| `criteria`   | Evaluation criteria for critique       | `quality and correctness`  | string |

Loops until critic says "PASS" or max rounds reached.

---

## Composition Primitives

### foreach

Aliases: `for_each`, `foreach_llm` (the `foreach_llm` alias defaults `sub_step_type=llm_call` when omitted).

| Parameter        | Description                               | Default    | Values |
|------------------|-------------------------------------------|------------|--------|
| `delimiter`      | Separator to split input into items       | `\n---\n`  | string |
| `sub_step_type`  | Step type for each item                   | `parallel` | any step type |
| `sub_target_role`| Target role for sub-steps                 | step's `target_role` | role id |
| `sub_param_{key}`| Extra parameters forwarded to sub-steps   | —          | dynamic prefix |

### parallel (parallel_fanout)

| Parameter         | Description                              | Default | Values |
|-------------------|------------------------------------------|---------|--------|
| `workers`         | Comma-separated list of role IDs         | —       | string |
| `parallel_count`  | Worker count if `workers` not set        | `3`     | integer |
| `vote_step_type`  | Optional follow-up consensus step type   | `""`    | any step type |
| `vote_param_{key}`| Parameters forwarded to the vote step    | —       | dynamic prefix |

### race

| Parameter | Description                              | Default | Values |
|-----------|------------------------------------------|---------|--------|
| `workers` | Comma-separated list of role IDs         | —       | string |
| `count`   | Worker count if `workers` not set        | `2`     | integer (1–10) |

### map_reduce

Aliases: `mapreduce`, `map_reduce_llm` (the `map_reduce_llm` alias defaults both map/reduce step types to `llm_call` when omitted).

| Parameter             | Description                         | Default    | Values |
|-----------------------|-------------------------------------|------------|--------|
| `delimiter`           | Separator to split input            | `\n---\n`  | string |
| `map_step_type`       | Step type for map phase             | `llm_call` | any step type |
| `map_target_role`     | Target role for map workers         | step's `target_role` | role id |
| `reduce_step_type`    | Step type for reduce phase          | `llm_call` | any step type |
| `reduce_target_role`  | Target role for reducer             | step's `target_role` | role id |
| `reduce_prompt_prefix`| Text prepended to reduce prompt     | `""`       | string |

### workflow_call

| Parameter  | Description                          | Default | Values |
|------------|--------------------------------------|---------|--------|
| `workflow`  | Name of the sub-workflow to invoke  | —       | string (required) |

### vote_consensus

No parameters. Uses input directly (typically from parallel fan-out output).

---

## Integration Primitives

### connector_call

Aliases:
- `bridge_call` (semantic alias)
- `cli_call` (semantic alias)
- `mcp_call` (semantic alias; if `tool` is set and `operation` is omitted, parser fills `operation=<tool>`)
- `http_get` / `http_post` / `http_put` / `http_delete` (parser fills `method=GET/POST/PUT/DELETE` when omitted)

| Parameter           | Description                              | Default  | Values |
|---------------------|------------------------------------------|----------|--------|
| `connector`         | Connector name (required)                | —        | string |
| `method`            | HTTP method (mainly for HTTP connectors) | connector default | `GET`, `POST`, `PUT`, `DELETE`, ... |
| `operation`         | Operation/method on the connector        | `""`     | string |
| `retry`             | Retry attempts on failure                | `0`      | integer (0–5) |
| `timeout_ms`        | Timeout in ms                            | `30000`  | integer (100–300000) |
| `optional`          | If true, missing connector is non-fatal  | `false`  | `true`, `false` |
| `on_missing`        | Action when connector not found          | `fail`   | `fail`, `skip` |
| `on_error`          | Action on connector error                | `fail`   | `fail`, `continue` |
| `allowed_connectors`| Comma-separated whitelist for validation | `""`     | string |

### emit

| Parameter    | Description                        | Default  | Values |
|--------------|------------------------------------|----------|--------|
| `event_type` | Custom event type identifier       | `custom` | string |
| `payload`    | Event payload (defaults to input)  | input    | string |

---

## Human Primitives

### human_input

| Parameter    | Description                     | Default                  | Values |
|--------------|---------------------------------|--------------------------|--------|
| `prompt`     | Message shown to the human      | `Please provide input:`  | string |
| `variable`   | Variable name to store input    | `user_input`             | string |
| `timeout`    | Timeout in seconds              | `1800`                   | integer |
| `on_timeout` | Action on timeout               | `fail`                   | `fail`, `skip` |

### human_approval

| Parameter   | Description                       | Default               | Values |
|-------------|-----------------------------------|-----------------------|--------|
| `prompt`    | Message shown to reviewer         | `Approve this step?`  | string |
| `timeout`   | Timeout in seconds                | `3600`                | integer |
| `on_reject` | Action if rejected                | `fail`                | `fail`, `skip`, `branch` |
