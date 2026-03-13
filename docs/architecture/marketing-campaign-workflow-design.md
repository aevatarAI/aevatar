# Marketing Campaign Workflow Design

## Goal

Define a complete marketing content workflow in Aevatar that accepts free-form user input, discovers brand and competitor context, pauses for human approval at key checkpoints, generates strategy and assets, reviews outputs, iterates weak assets, and delivers approved assets into a content library.

This design keeps:

- search and extraction in external connectors
- reasoning and generation in `llm_call`
- user review in `human_input` / `human_approval`
- orchestration in workflow YAML

## User Input Model

The user should provide **free-form text**, not a strict JSON schema.

Example:

```text
帮我分析 Basecamp 的官网和竞品，做一套 TikTok、Instagram 的内容策略。
重点看 pricing、customer proof 和产品卖点。
最后生成 UGC 脚本、静态图文方向和短视频创意。
```

Internally, the workflow should normalize the text into variables such as:

- `brand_name`
- `brand_url`
- `brand_manual_text`
- `competitor_urls`
- `campaign_goal`
- `channels`
- `asset_types`

The normalization step is not exposed to the user.

## High-Level Flow

```text
free-form input
-> request parsing
-> brand/source resolution
-> discovery
-> approval checkpoint 1
-> strategy
-> approval checkpoint 2
-> generation
-> review / iteration
-> delivery
```

## Stage Breakdown

### 1. Input

Purpose:

- capture raw user intent
- parse brand URL, brand name, channels, goals, asset types
- resolve whether the user provided:
  - a URL
  - brand text / manual
  - both
  - neither

Recommended primitives:

- `assign`
- `llm_call`
- `switch`

Suggested internal variables:

- `raw_request`
- `parsed_request`
- `brand_url`
- `brand_name`
- `campaign_goal`
- `channels`
- `asset_types`

### 2. Discovery

Purpose:

- discover official brand pages
- discover competitor references
- extract homepage / pricing / features / customers content
- synthesize a discovery report

Recommended tool split:

- `marketing_search_mcp` -> Tavily
- `marketing_extract_mcp` -> `chrono-cheerio`

Recommended primitives:

- `connector_call`
- `assign`
- `llm_call`

Expected outputs:

- `brand_sources`
- `competitor_sources`
- `research_bundle`
- `discovery_report`

### 3. Approval Checkpoint 1

Purpose:

- let the user confirm whether the discovered brand understanding, competitors, and evidence are correct

Recommended primitive:

- `human_input`

Do not ask the user to produce strict JSON manually.
Instead:

1. show a compact markdown review packet
2. collect natural-language feedback
3. use one `llm_call` to normalize the response into a routing decision

Suggested review prompt to user:

```text
Review the discovery output.
Reply with:
- approve
- revise: <what to fix>
- stop
```

Suggested normalized decision object:

```text
decision=approve|revise|stop
feedback=<text>
scope_adjustment=<text>
```

Recommended routing:

- `approve` -> strategy
- `revise` -> rerun discovery synthesis or source selection
- `stop` -> end workflow

### 4. Strategy

Purpose:

- generate content pillars
- propose hooks / angles
- propose per-platform recommendations
- define asset plan

Recommended primitives:

- `llm_call`

Expected outputs:

- `strategy_report`
- `asset_plan`
- `hook_library`
- `platform_plan`

### 5. Approval Checkpoint 2

Purpose:

- let the user approve or adjust the strategy before expensive asset generation

Recommended primitive:

- `human_input`

Suggested review prompt:

```text
Review the strategy output.
Reply with:
- approve
- revise: <what to fix>
- narrow_scope: <what to keep>
- stop
```

Suggested normalized decision object:

```text
decision=approve|revise|narrow_scope|stop
feedback=<text>
scope_adjustment=<text>
```

Recommended routing:

- `approve` -> generation
- `revise` -> rerun strategy
- `narrow_scope` -> rewrite `channels` / `asset_types` / `asset_plan`, then continue
- `stop` -> end workflow

### 6. Generation

Purpose:

- produce asset specs and draft assets

Asset types currently envisioned:

- video
- static
- ugc

Recommended split:

- `llm_call` generates structured asset specs first
- then per asset type:
  - video: script, beat sheet, shot list
  - static: concept, headline, caption, visual brief
  - ugc: creator brief, hook, talking points, CTA

Recommended primitives:

- `llm_call`
- `foreach`
- `parallel`
- optional `connector_call` for external production services

Expected outputs:

- `asset_specs`
- `generated_assets`

### 7. Review / Iteration

Purpose:

- score generated assets
- automatically revise weak assets

Recommended primitives:

- `evaluate`
- `reflect`
- `conditional`
- `foreach`

Suggested review roles:

- `qa_reviewer`
- `platform_reviewer`
- `brand_guardian`

Suggested scoring dimensions:

- brand fit
- clarity
- platform fit
- hook strength
- CTA quality
- evidence safety

Recommended rule:

- if score >= threshold -> keep asset
- if score < threshold -> revise asset
- stop after max revision rounds

Expected outputs:

- `review_results`
- `approved_assets`
- `rejected_assets`

### 8. Delivery

Purpose:

- package approved assets for downstream use

Delivery options:

- markdown bundle
- json export
- content library storage
- download archive
- publish connector

Recommended primitives:

- `connector_call`
- `assign`

Expected outputs:

- `delivery_manifest`
- `content_library_record`

## Approval Interaction Design

Approval should not be modeled as a simple binary yes/no gate.
It should be modeled as:

1. a human-readable review packet
2. natural-language reviewer feedback
3. an `approval_interpreter` `llm_call`
4. a routing step using `switch`

This provides:

- low-friction UX
- structured control for the workflow
- reusable decision handling

### Recommended pattern

```text
report
-> human_input
-> approval_interpreter (llm_call)
-> switch on decision
```

### Decision vocabulary

Recommended decision tokens:

- `approve`
- `revise`
- `narrow_scope`
- `stop`

### Why `human_input` over bare `human_approval`

`human_approval` is suitable for pure yes/no approval.
This workflow needs richer responses:

- revision guidance
- scope changes
- stop conditions

Therefore `human_input` is the better primary primitive, with `llm_call` normalizing the result.

## Role Suggestions

Recommended roles:

- `request_parser`
- `web_researcher`
- `discovery_analyst`
- `marketing_strategist`
- `copywriter`
- `asset_producer`
- `qa_reviewer`
- `approval_interpreter`
- `delivery_operator`

## Connector Suggestions

Recommended connectors:

- `marketing_search_mcp`
  - Tavily search / discovery
- `marketing_extract_mcp`
  - `chrono-cheerio` extraction service
- `marketing_library_mcp`
  - content library / storage / export
- optional future connectors:
  - `marketing_video_mcp`
  - `marketing_image_mcp`
  - `marketing_publish_mcp`

## Recommended First Implementation Scope

Do not build the entire video/static/ugc delivery stack first.

Recommended v1:

1. free-form input parsing
2. discovery
3. approval checkpoint 1
4. strategy
5. approval checkpoint 2
6. asset spec generation
7. review / iteration
8. markdown/json delivery

This keeps the pipeline demonstrable without requiring image/video generation infrastructure on day one.

## Next Implementation Unit

The next concrete implementation should be:

- one end-to-end orchestrator workflow YAML
- free-form input parsing
- two human checkpoints
- multi-page discovery
- strategy report
- asset spec generation
- reviewer loop
- markdown delivery

This is sufficient to validate the full human-in-the-loop architecture before integrating real publishing or media generation services.
