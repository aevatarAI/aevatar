# Workflow YAML Examples

Working examples organized by pattern complexity.

---

## 1. Pure Data Pipeline (no LLM)

Validates input, assigns to variable, retrieves facts, counts results.

```yaml
name: data_pipeline
description: Guard → Assign → Retrieve → Transform

roles: []

steps:
  - id: validate
    type: guard
    parameters:
      check: "not_empty"
      on_fail: "fail"
    next: save

  - id: save
    type: assign
    parameters:
      target: "raw_input"
      value: "$input"
    next: find

  - id: find
    type: retrieve_facts
    parameters:
      query: "speed light"
      top_k: "2"
    next: count

  - id: count
    type: transform
    parameters:
      op: "count"
```

---

## 2. Transform Operations

```yaml
name: transform_demo
description: Uppercase then count lines.

roles: []

steps:
  - id: to_upper
    type: transform
    parameters:
      op: "uppercase"
    next: count

  - id: count
    type: transform
    parameters:
      op: "count"
```

---

## 3. Guard Validation Chain

```yaml
name: guard_chain
description: Validate non-empty, valid JSON, contains "@".

roles: []

steps:
  - id: check_not_empty
    type: guard
    parameters:
      check: "not_empty"
      on_fail: "fail"
    next: check_json

  - id: check_json
    type: guard
    parameters:
      check: "json_valid"
      on_fail: "fail"
    next: check_email

  - id: check_email
    type: guard
    parameters:
      check: "contains"
      keyword: "@"
      on_fail: "fail"
```

---

## 4. Switch (Multi-way Branch)

```yaml
name: ticket_router
description: Route tickets by category keyword.

roles: []

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

---

## 5. Single LLM Call

```yaml
name: single_llm
description: Send prompt to an assistant role.

roles:
  - id: assistant
    system_prompt: |
      You are a concise technical assistant. Answer in 2-3 sentences.

steps:
  - id: answer
    type: llm_call
    role: assistant
```

---

## 6. Multi-role LLM Chain

```yaml
name: llm_chain
description: Analyst identifies problems, Advisor proposes solutions.

roles:
  - id: analyst
    system_prompt: |
      Identify the top 3 challenges. Use bullet points, max 2 sentences each.

  - id: advisor
    system_prompt: |
      Given a list of challenges, propose a solution for each.
      One paragraph per challenge.

steps:
  - id: analyze
    type: llm_call
    role: analyst
    next: propose

  - id: propose
    type: llm_call
    role: advisor
```

---

## 7. Parallel Fan-out

```yaml
name: parallel_brainstorm
description: Fan-out to 3 workers with different perspectives.

roles:
  - id: worker_a
    system_prompt: "You are a pragmatic engineer. 2-3 bullet points."
  - id: worker_b
    system_prompt: "You are a business strategist. 2-3 bullet points."
  - id: worker_c
    system_prompt: "You are a DevOps specialist. 2-3 bullet points."

steps:
  - id: brainstorm
    type: parallel
    parameters:
      workers: "worker_a,worker_b,worker_c"
```

---

## 8. Race (First-wins)

```yaml
name: race_demo
description: 3 racers answer the same question; first response wins.

roles:
  - id: fast_a
    system_prompt: "Answer in one sentence. Be precise and technical."
  - id: fast_b
    system_prompt: "Answer in one sentence. Use a real-world analogy."
  - id: fast_c
    system_prompt: "Answer in one sentence. Keep it simple for beginners."

steps:
  - id: race_answer
    type: race
    parameters:
      workers: "fast_a,fast_b,fast_c"
```

---

## 9. Map-Reduce

```yaml
name: topic_analysis
description: Split topics, map each to bullet points, reduce to summary.

roles:
  - id: mapper
    system_prompt: "Given a topic, write exactly 3 concise bullet points."
  - id: reducer
    system_prompt: "Synthesize bullet points into a single paragraph under 100 words."

steps:
  - id: analyze
    type: map_reduce
    parameters:
      delimiter: "\n---\n"
      map_step_type: "llm_call"
      map_target_role: "mapper"
      reduce_step_type: "llm_call"
      reduce_target_role: "reducer"
      reduce_prompt_prefix: "Synthesize these points:"
```

---

## 10. ForEach

```yaml
name: describe_each
description: For each technology, generate a one-line description.

roles:
  - id: describer
    system_prompt: |
      Given a technology name, write a single sentence about what it is.

steps:
  - id: describe
    type: foreach
    parameters:
      delimiter: "\n---\n"
      sub_step_type: "llm_call"
      sub_target_role: "describer"
```

---

## 11. Evaluate (LLM-as-Judge)

```yaml
name: write_and_judge
description: Writer produces content, Judge scores it.

roles:
  - id: writer
    system_prompt: "You are a creative poet."
  - id: judge
    system_prompt: |
      You are a literary critic. Respond with ONLY a single number (the score).

steps:
  - id: write
    type: llm_call
    role: writer
    next: judge_it

  - id: judge_it
    type: evaluate
    role: judge
    parameters:
      criteria: "creativity, structure, and adherence to format"
      scale: "1-5"
      threshold: "3"
```

---

## 12. Reflect (Self-improvement Loop)

```yaml
name: reflect_demo
description: Draft, critique, and refine up to 3 rounds.

roles:
  - id: thinker
    system_prompt: |
      When improving, incorporate feedback.
      When reviewing, say "PASS" if good, or explain what needs fixing.

steps:
  - id: self_improve
    type: reflect
    role: thinker
    parameters:
      max_rounds: "3"
      criteria: "clarity, accuracy, and beginner-friendliness"
```

---

## 13. Cache

```yaml
name: cached_llm
description: Cache LLM responses to avoid redundant calls.

roles:
  - id: assistant
    system_prompt: "You are a database expert. 2-3 sentences."

steps:
  - id: cached_answer
    type: cache
    role: assistant
    parameters:
      cache_key: "sql_vs_nosql"
      ttl_seconds: "300"
      child_step_type: "llm_call"
      child_target_role: "assistant"
```

---

## 14. Connector Call with Retry

```yaml
name: api_call
description: Call external API with retry and graceful fallback.

roles: []

steps:
  - id: fetch_data
    type: connector_call
    parameters:
      connector: "weather_api"
      operation: "get_forecast"
      retry: "3"
      timeout_ms: "10000"
      on_error: "continue"
    on_error:
      strategy: fallback
      fallback_step: use_default

  - id: use_default
    type: assign
    parameters:
      target: "forecast"
      value: "Data unavailable"
```

---

## 15. Human-in-the-Loop

```yaml
name: human_review
description: Generate content, get human approval before publishing.

roles:
  - id: writer
    system_prompt: "Draft a professional email."

steps:
  - id: draft
    type: llm_call
    role: writer
    next: review

  - id: review
    type: human_approval
    parameters:
      prompt: "Review this email draft. Approve to send."
      timeout: "3600"
      on_reject: "fail"
```
