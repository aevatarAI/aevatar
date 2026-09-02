---
title: "Workflow Mission Wall Product Package"
status: draft
owner: tbd
last_updated: 2026-06-30
references:
  - "../adr/0015-agui-sse-projection-session-pipeline.md"
  - "../adr/0023-two-tier-inspector-architecture.md"
  - "../canon/observability.md"
  - "./2026-04-30-studio-invoke-chat-like-run-diagnostics.md"
---

# Workflow Mission Wall Product Package

## 1. Executive Summary

Stakeholder request:

> 在屏幕上实时看到 workflow 在怎么跑，干了什么，这个 Team 在处理什么信息：日志或者其他展示方式。workflow 执行看板看看外面开源 / 付费项目，还有增加执行拓扑图，看消息流。能直接用就直接用，不能直接用就抄成熟项目的形态。

Product decision:

1. Build a **Workflow Mission Wall** for large-screen shared visibility.
2. Keep a separate **Run Inspector** for drilldown debugging.
3. Reuse Aevatar's existing durable readmodel and live observation pipeline.
4. Reuse existing Console workflow/run components before inventing a new topology surface.
5. Copy mature information architecture from Kestra, Temporal, Airflow, Dify, LangGraph Studio, LangSmith, Langfuse, Phoenix, Grafana, Datadog, and New Relic.
6. Do not fork or embed full third-party UIs whose backend model or license does not fit Aevatar.

Current design decisions:

1. The wall assumes **multiple workflow runs are happening at the same time**.
2. The left rail is a **Live Run Window**, not an active-definition list. It contains running, recently completed, and priority-pinned published runs.
3. The center graph shows one **focus run** at a time. The focus is chosen by deterministic wall-director rules, not by an AI black box.
4. Short completed runs remain visible for a retention window so they do not disappear before viewers notice them.
5. Failed/timed-out state comes from the run/report `completionStatus` readmodel. Timeline events explain failures; they do not define current run state.
6. Top-level freshness is displayed as age, for example `Fresh 2s`. A `v42`-style version is shown only for the selected run.
7. After stakeholder review, the MVP wall has no separate priority list and no event feed area. Waiting, failed, retrying, and stale states appear as run-card badges, graph badges, and focus reasons.

The Mission Wall is not a log viewer. It is a big-screen execution map:

```text
+--------------------------------------------------------------------------------+
| Live | Running Runs | Waiting Human | Failed | Avg Latency | Freshness |
+----------------------+---------------------------------------------------------+
| Live Run Window      |        Real-time Execution Topology                      |
| Team / workflow/run  |        Workflow step graph + run trace                   |
| status and stage     |        Focus reason + animated message flow              |
+----------------------+---------------------------------------------------------+
+--------------------------------------------------------------------------------+
```

The Run Inspector is the detail surface:

```text
Overview | Topology | Timeline/Gantt | Messages | Logs | Events | State | Outputs | Metrics | Trace Links
```

Product terminology boundary:

1. The wall's primary user-facing objects are **Team**, **Workflow Run**, **Entry Member**, **Member / Role**, **Step**, **Tool / Connector**, **Human Gate**, and **Projection / ReadModel**.
2. `Actor`, `GAgent`, `rootActorId`, and `primaryActorId` are runtime implementation terms. They may appear in Run Inspector runtime/debug sections, but they are not the wall's default labels.
3. When existing backend data is keyed by actor/runtime ids, the presentation adapter should map those ids to Team/Member labels from Studio and service readmodels before rendering the wall.

Reuse boundary:

1. The MVP wall should reuse the existing workflow step graph shape used by `GraphCanvas` and `MemberPublishedRunsReplay`: workflow steps are the primary nodes, with execution status overlaid on each step.
2. `MissionControl/TopologyCanvas` remains useful for runtime topology and live observation, but it should not be the MVP's only source of truth for "how this workflow runs" because the project already has workflow-step replay surfaces.
3. `RunsTracePane` and existing runtime conversation/event presentation should remain drilldown surfaces for Timeline / Messages / Events, not MVP wall regions.

## 2. Benchmark Summary

### 2.1 Workflow Orchestration

| Product | Category | OSS / Paid | License / Risk | What To Use Directly | What To Copy | What Not To Copy | Relevance |
|---|---|---:|---|---|---|---|---|
| Kestra | Workflow orchestration | OSS + Enterprise | Apache 2.0 for public repo; enterprise features separate | Nothing directly except concepts | Execution page structure: Overview, Gantt, Logs, Topology, Outputs, Metrics, Dependencies | Do not embed its UI/backend; task model differs | Best reference for an execution board |
| Temporal UI | Durable workflow UI | OSS | MIT in `temporalio/ui` | Pattern only | Workflow detail: History, Timeline, Compact, JSON, Relationships, Workers, Pending Activities | Do not reuse backend-specific UI as-is; Temporal history model differs | Best reference for event history and relationships |
| Apache Airflow | DAG orchestration | OSS | Apache 2.0 | Pattern only | Grid view, graph view, task logs, run status matrix | Do not copy dense DAG admin UI into big screen | Best reference for recent-run status matrix |
| Dagster | Data orchestration | OSS + paid | Apache 2.0 core | Pattern only | Structured event logs, asset/job/run overview, run filtering | Do not copy asset-first model as primary IA | Good reference for structured logs and run list |
| Prefect | Workflow orchestration | OSS + paid | Apache 2.0 core | Pattern only | Flow run states, logs, work pools, automations | Do not make orchestration admin concepts primary on wall | Useful for run state language |
| Netflix Conductor / Orkes | Microservice workflow | OSS + paid Orkes | Apache 2.0 for Conductor; paid Orkes | Pattern only | Task-level input/output, retry, timing, visual execution debugging | Do not adopt its workflow schema | Good reference for service workflow execution details |
| Camunda Operate | BPMN/process monitoring | Paid/platform | Commercial | Pattern only | Process instance diagram with incidents, variables, current token | Do not force BPMN semantics onto Aevatar execution | Good reference for incident-oriented process wall |

### 2.2 AI Workflow And Agent Builders

| Product | Category | OSS / Paid | License / Risk | What To Use Directly | What To Copy | What Not To Copy | Relevance |
|---|---|---:|---|---|---|---|---|
| Dify | AI workflow builder | OSS/source-available + cloud | License changed from plain Apache-style terms; avoid code copy without review | Pattern only | Workflow run history, tracing, node execution path, node latency and output summaries | Do not copy code or license-sensitive UI wholesale | Strong reference for AI workflow node tracing |
| Flowise | AI agent/workflow builder | OSS + paid | Apache 2.0 core as of current public repo; verify before reuse | Pattern only | Visual agent flow, node execution logs, tool/chain steps | Do not inherit chatflow-specific data model | Useful for AI tool/agent graph |
| LangGraph Studio / LangSmith Studio | Agent graph IDE | Paid / platform | Commercial service | Pattern only | Graph mode + chat mode; inspect traversed nodes and state | Do not copy platform-specific interactions as requirements | Best reference for agent graph + conversation split |
| n8n | Workflow automation | Source-available + paid | Sustainable Use License; no code copy without legal review | Pattern only | Canvas execution replay, node status, manual trigger/testing | Do not copy code; license risk | Good reference for node canvas execution animation |

### 2.3 LLM Observability

| Product | Category | OSS / Paid | License / Risk | What To Use Directly | What To Copy | What Not To Copy | Relevance |
|---|---|---:|---|---|---|---|---|
| Langfuse | LLM observability | OSS + cloud | MIT in public repo; verify version | External link or deployment option | Hierarchical trace: span tree, LLM calls, tool calls, retrieval, cost, latency, session | Do not expose raw prompts on big screen | Strong reference for Team information processing |
| Arize Phoenix | LLM observability | OSS + hosted | Apache 2.0 | External deep trace option | OpenTelemetry trace/span model, LLM/tool/retrieval inspection | Do not make Phoenix the big-screen primary UI | Good fit for span-level drilldown |
| LangSmith | Agent observability | Paid/platform | Commercial | External link if adopted | Runs/traces/threads/session view, graph + conversation debugging | Do not depend on proprietary platform for core wall | Strong reference for agent run semantics |
| Helicone | LLM observability | OSS + paid | Verify license before direct reuse | External option only | Request logs, sessions, user analytics, latency/cost | Do not make LLM proxy logs equal workflow state | Useful secondary reference |

### 2.4 Operations Wall And Dashboards

| Product | Category | OSS / Paid | License / Risk | What To Use Directly | What To Copy | What Not To Copy | Relevance |
|---|---|---:|---|---|---|---|---|
| Grafana | Dashboard / observability | OSS + Enterprise | AGPL for Grafana; plugins vary | External dashboard / kiosk option, not embedded code | Kiosk mode, playlists, Canvas, Node Graph, large-screen visual rules | Do not build Aevatar wall as generic metric dashboard | Best big-screen operational reference |
| Datadog Dashboards | Observability dashboard | Paid | Commercial | Pattern only | TV mode: fullscreen, no scroll, all widgets visible | Do not depend on Datadog-specific widgets | Good large-screen layout reference |
| New Relic Dashboards | Observability dashboard | Paid | Commercial | Pattern only | TV/fullscreen mode and dashboard rotation | Do not copy generic monitoring wall as product | Useful big-screen operations reference |
| Jaeger / Tempo | Distributed tracing | OSS / mixed | Apache 2.0 for Jaeger; Tempo AGPL | External trace links | Trace waterfall, span hierarchy, service dependency | Do not make trace waterfall the wall's main view | Engineering drilldown, not big-screen primary |

## 3. What To Copy

### 3.1 Kestra: Execution Page Structure

Copy:

```text
Overview
Gantt / Timeline
Logs
Topology
Outputs
Metrics
Dependencies
```

Aevatar translation:

```text
Mission Wall:
  status strip, live run window, focus reason, workflow step topology

Run Inspector:
  Overview | Topology | Timeline/Gantt | Logs | Outputs | Metrics | Dependencies
```

Why:

Kestra treats one execution as a first-class inspectable object. That maps well to Aevatar's `WorkflowRunInsightReport`.

### 3.2 Temporal: History And Relationships

Copy:

1. History as a first-class tab.
2. Timeline / Compact / Raw JSON modes.
3. Relationships for parent/child workflows.
4. Worker / pending activity surface.

Aevatar translation:

```text
History:
  Timeline
  Compact
  All events
  Raw event envelope

Relationships:
  parent workflow
  child workflow
  member/runtime links
  command correlation
  readmodel observation
```

Why:

Aevatar has member context, runtime links, sub-workflows, command ids, correlation ids, and projection state. Temporal's shape is useful, even though the backend model differs.

### 3.3 Airflow: Recent Run Matrix

Copy:

```text
Rows: task/step/member
Columns: recent runs
Cells: state color
Click: drilldown to logs/timeline
```

Aevatar translation:

```text
Rows: workflow step / member / role
Columns: recent workflow runs
Cells: running, completed, failed, waiting, retrying, stale
```

Why:

The big screen should show trend and health, not only the current run.

### 3.4 Dify And n8n: Node Canvas Execution Replay

Copy:

1. Nodes light up in execution order.
2. Node drawer shows input, output, error, latency.
3. Failed node is visually obvious.
4. Run history can replay or inspect a previous execution.

Aevatar translation:

```text
Node:
  team / entry member / member / role / step / tool / approval / projection

Drawer:
  input summary
  output summary
  error
  latency
  state version
  trace link
```

Why:

The stakeholder specifically asked for message flow and execution topology.

### 3.5 LangGraph Studio And LangSmith: Graph + Conversation

Copy:

1. Graph mode for execution structure.
2. Chat/conversation mode for AI output.
3. Intermediate state and traversed nodes.

Aevatar translation:

```text
Mission Wall:
  graph is primary
  focus reason and node badges summarize what is happening

Run Inspector:
  graph tab + conversation tab + state tab
```

Why:

Aevatar teams process information through members and their bound implementations. The wall needs both topology and "what information is being processed".

### 3.6 Langfuse And Phoenix: LLM Trace Semantics

Copy:

1. Trace tree / spans.
2. LLM call, tool call, retrieval, custom step.
3. Latency, token/cost, error, status.
4. Session/user grouping.

Aevatar translation:

```text
Trace categories:
  reasoning summary
  tool call
  connector call
  retrieval
  human approval
  projection
```

Why:

"Team 在处理什么信息" is not raw logs. It is structured semantic trace.

### 3.7 Grafana / Datadog / New Relic: Big-Screen Rules

Copy:

1. Fullscreen / kiosk mode.
2. No primary scroll.
3. All key widgets visible at once.
4. Strong status color and typography.
5. Auto-refresh and honest degraded state.
6. Dashboard rotation only for secondary views.

Aevatar translation:

```text
Mission Wall:
  10-foot readability
  3-second comprehension
  automatic live refresh
  no dense tables
  no raw payload
```

## 4. What To Use Directly

External component conclusion:

1. `@xyflow/react` / React Flow is the direct-use component for the MVP topology canvas. It is already installed in Console Web and is documented as MIT-licensed open source.
2. `elkjs` is the recommended direct-use layout engine for long directed workflow graphs. Its layer-based layout is designed for node-link diagrams with a dominant direction, which matches workflow step graphs.
3. AntV X6 is also MIT-licensed and is a credible open-source fallback, but introducing it now would duplicate the graph stack we already have.
4. GoJS and yFiles are mature paid diagram SDKs. They should be evaluated only if Aevatar later needs advanced diagram editing, automatic layout, BPMN-grade process diagrams, or commercial support that React Flow + ELK cannot cover.
5. Full workflow/observability products such as Temporal UI, Kestra, Dify, n8n, Grafana, Langfuse, or LangSmith should not be embedded as the wall. Their backend semantics and product surfaces do not match Aevatar's Team/Member/Workflow Run model; copy IA patterns and link out for drilldown instead.

| Direct Use | Decision | Rationale |
|---|---|---|
| `@xyflow/react` / React Flow | Direct-use component for topology | MIT-licensed open-source React component; already in `apps/aevatar-console-web`; fits interactive node-edge canvas |
| `elkjs` / Eclipse ELK layered layout | Direct-use layout engine for long directed workflow graphs | Layer-based layout is suited to directed node-link diagrams; use it to compute coordinates before passing nodes to `GraphCanvas` |
| `GraphCanvas` studio variant | Directly reuse as wall primary workflow graph wrapper | Already renders workflow steps with `executionStatus` and `executionFocused` |
| `MemberPublishedRunsReplay` graph/audit mapping | Reuse as the closest implementation pattern | Already maps published run audit steps into step graph, log list, selected step detail |
| Aevatar readmodels | Use as durable truth | Existing architecture already requires query from readmodel |
| AGUI/SSE | Use for current-run live deltas and animation | Existing streaming path; do not make it durable truth |
| OTel / Jaeger / Phoenix / Langfuse links | Use as external deep trace links | Good for engineering drilldown, not wall primary |
| `RunsTracePane` | Reuse for Run Inspector only in MVP | Timeline / Messages / Events already exist; the wall primary view stays topology-first |
| `MissionControl/TopologyCanvas` | Reuse selectively for live runtime topology view | It has node status and animated edges, but is secondary to workflow step graph for MVP |

Direct component decision:

1. **Use React Flow / `@xyflow/react` through existing `GraphCanvas`** for the MVP. This is the external graph renderer we should directly embed for the wall.
2. **Add `elkjs` as the layout engine when step count or branching makes manual positioning unreadable.** React Flow renders nodes/edges; ELK computes stable layered positions.
3. **Do not buy/build a second diagram component for MVP.** AntV X6 is a viable MIT alternative, but adopting it would duplicate the existing React Flow stack.
4. **Do not adopt commercial diagram SDKs such as GoJS or yFiles for MVP** unless the team later needs advanced diagram editing/layout features that React Flow + ELK cannot cover.
5. **Do not embed full workflow products such as Temporal UI, Kestra, Dify, n8n, Grafana, or Langfuse** into the wall. Copy their interaction patterns; link out to external tools for drilldown when useful.
6. **If stakeholders ask whether a market component can be directly used, the MVP answer is yes for the topology canvas and layout**: use the already-installed `@xyflow/react` / React Flow component, the existing `GraphCanvas` wrapper, and add ELKjs for automatic layout instead of self-developing a graph engine.

## 5. What Not To Copy

1. Do not fork Temporal/Kestra/Airflow/Dify/n8n UI wholesale.
2. Do not use external workflow engine data models as Aevatar's internal model.
3. Do not put raw JSON, raw `EventEnvelope`, raw OTel spans, or server logs on the big-screen primary surface.
4. Do not show full prompts, credentials, secure inputs, authorization headers, or raw model reasoning on the wall.
5. Do not reconstruct state from logs, OTel buffers, or in-memory registries.
6. Do not add a second projection/log pipeline for the wall.
7. Do not turn the big screen into an engineering-only console.

## 6. Problem Framing

### 6.1 The Real Product Problem

The request "show logs or some other display" is not primarily a logging problem.

It is an execution-state visibility problem:

```text
Can a viewer understand, from a shared screen, what the AI team is doing,
how the workflow is progressing, where messages are flowing, and what needs intervention?
```

### 6.2 Viewers And Context

| Viewer | Distance | Situation | Needs |
|---|---:|---|---|
| Boss / stakeholder | 2-5m | Demo, review, live operations | See that workflows are alive, useful, and understandable |
| Operator / support | 1-3m | Monitoring running and recently completed workflow runs | Find failed, waiting, or stale runs |
| Engineer | Desk + wall | Debugging after alert | Jump from wall into Run Inspector / trace |
| Product / design | 1-3m | Product review | Validate whether "Team processing information" is visible |

### 6.3 3-Second Comprehension Target

Within 3 seconds, a viewer should answer:

1. Is the system live, degraded, or disconnected?
2. How many published runs are running right now?
3. Are any workflows failed, waiting, or requiring human input?
4. Which team/workflow has the most wall-visible activity?
5. Where is the current selected workflow in its topology?
6. What type of information is currently being processed?

### 6.4 Big Screen vs Drilldown

| Topic | Mission Wall | Run Inspector |
|---|---|---|
| Purpose | Shared situational awareness | Investigation |
| Density | Low | Medium/high |
| Interaction | Minimal | Click/filter/copy/search |
| Primary visual | Topology + status | Tabs + detail panels |
| Logs | No primary log area; safe summaries only | Structured logs + raw logs |
| Raw events | No | Yes, advanced tab |
| Trace payload | No | Yes, through trace links |
| Sensitive data | Summarized/masked | Permissioned and masked |

### 6.5 Durable vs Live Data Boundary

Durable truth:

```text
Committed domain events -> Projection Pipeline -> ReadModels
```

Live observation:

```text
AGUI/SSE/OTel -> current-screen animation and transient deltas
```

Rules:

1. Mission Wall must recover stable state from readmodels after refresh.
2. Live streams may animate message flow but cannot define business truth.
3. Raw logs and traces are drilldown only.
4. Stream disconnect must be shown honestly as degraded live observation.

## 7. Positioning

Product name:

```text
Workflow Mission Wall
```

One-line positioning:

```text
A large-screen operations wall that shows Aevatar teams, workflow runs, members, steps, tools, and human gates moving through live execution, with priority states and message flow visible at a glance.
```

Product pair:

```text
Workflow Mission Wall = big-screen situational awareness
Run Inspector = per-run engineering and operator drilldown
```

## 8. Six-Frame Storyboard

### Frame 1: Idle / Overview

Screen state:

```text
Top strip shows Live, 0 failed, 2 waiting, 8 running runs, readmodel freshness 2s.
Left Live Run Window lists running, recently completed, and priority-pinned published runs.
Center workflow step graph shows the current focus run in calm state.
Graph header explains: Focused because latest running run · updated 12s ago.
```

User interpretation:

The system is alive and healthy.

Key UI elements:

1. Live indicator.
2. Running run count.
3. Failed/waiting counters.
4. Freshness badge.
5. Low-motion topology.

Data source:

`WorkflowExecutionCurrentState`, `WorkflowRunInsightReport`, graph readmodel, projection freshness.

Design risk:

Too many workflows may crowd the topology. Need clustering and "selected team" mode.

### Frame 2: Workflow Starts

Screen state:

```text
A new run enters the left rail.
Center topology creates a run node and first step node.
The command edge glows once.
Graph header says: Focused because new running run · command accepted.
```

User interpretation:

A workflow has begun and is accepted for execution.

Key UI elements:

1. New run card.
2. Run node.
3. Command accepted edge.
4. Focus reason badge.

Data source:

Command accepted receipt, AGUI/SSE run context, durable current state after projection.

Design risk:

ACK semantics must be honest: accepted is not completed.

### Frame 3: Team Members Process Information

Screen state:

```text
Research Member node pulses.
Step node label reads: "Collect sources".
Information category badge: "external documents".
Inspector preview shows summary only.
```

User interpretation:

The team is collecting information and the current role is visible.

Key UI elements:

1. Active team member or role node.
2. Current step label.
3. Information category.
4. Input summary, not raw prompt.

Data source:

Step request event, workflow timeline, typed run event payload.

Design risk:

Wall must not leak raw user input or sensitive prompt content.

### Frame 4: Tool Calls And Message Flow Animate

Screen state:

```text
Tool node "ChronoStorage" lights up.
Edge from Research Member to Tool animates.
Latency badge appears: 1.8s.
Result summary appears: "42 records".
```

User interpretation:

The workflow called a tool and got a result.

Key UI elements:

1. Tool/connector node.
2. Animated edge.
3. Latency badge.
4. Result summary.

Data source:

Tool call start/end event, connector call event, OTel span link if available.

Design risk:

Tool parameters may contain sensitive identifiers; wall shows only summary.

### Frame 5: Priority Event Appears

Screen state:

```text
Human Approval node breathes yellow.
Top strip Waiting Human increments.
The run card shows `WAIT`.
Graph header says: Focused because waiting for approval · 2m.
```

User interpretation:

The workflow is not broken, but it needs intervention.

Key UI elements:

1. Run-card priority badge.
2. Waiting node.
3. Counter update.
4. Focus reason.

Data source:

Human input/approval request event, current-state readmodel.

Design risk:

Need clear difference between waiting, failed, retrying, and stale.

### Frame 6: Drilldown

Screen state:

```text
Operator clicks or scans the run card.
Run Inspector opens with selected run context.
Tabs show Overview, Topology, Timeline, Messages, Logs, Events, State, Outputs, Metrics, Trace Links.
```

User interpretation:

The wall found the issue; the inspector explains it.

Key UI elements:

1. Run header with runId, commandId, memberId, implementation kind, stateVersion.
2. Timeline.
3. Node-level input/output summaries.
4. Raw events tab behind advanced affordance.
5. Trace links.

Data source:

`WorkflowRunInsightReport`, timeline readmodel, graph readmodel, AGUI event session, OTel trace link.

Design risk:

Drilldown must preserve context so users do not lose the selected node/run.

## 9. PRD

### 9.1 Product Name

Workflow Mission Wall.

### 9.2 One-Line Positioning

A large-screen execution wall for seeing how Aevatar workflows and AI teams are running, what they are processing, and where message flow or human intervention is needed.

### 9.3 Background And Stakeholder Request

The stakeholder wants a screen where workflow execution is visible in real time:

1. What is the workflow doing?
2. What information is the Team processing?
3. Which member/step/tool is active?
4. How are messages flowing?
5. Can logs or other displays make this visible?
6. What can be reused or copied from open-source and paid products?

The product answer is a wall plus drilldown. The wall is for shared situational awareness; the inspector is for investigation.

### 9.4 Users And Viewing Context

Primary:

1. Stakeholders watching demos or operations.
2. Operators monitoring running, recently completed, and blocked workflow runs.
3. Engineers responding to failed or stuck runs.

Viewing context:

1. Large TV or shared display.
2. Fullscreen / kiosk mode.
3. Minimal keyboard/mouse interaction.
4. Readable from 2-5 meters.
5. Auto-refreshing and honest about stale live streams.

### 9.5 Goals

1. Show running/recent workflow runs and team execution state in 3 seconds.
2. Show execution topology and message flow as the primary visual.
3. Surface failures, timeouts, retries, stale projections, and human input/approval.
4. Show business-readable information processing summaries.
5. Preserve durable truth/readmodel vs live animation boundary.
6. Provide direct drilldown to Run Inspector and external trace tools.
7. Reuse existing Aevatar execution readmodels, AGUI/SSE, and topology components.

### 9.6 Non-Goals

1. Do not replace the full Runs page in V1.
2. Do not create a new workflow engine or alternate execution pipeline.
3. Do not build a generic Grafana clone.
4. Do not show raw JSON/logs on the big screen.
5. Do not expose secrets, raw prompts, raw reasoning, credentials, or authorization headers.
6. Do not query actor state directly or replay events in query path.

### 9.7 Competitive Patterns To Copy

| Pattern | Source | Aevatar Feature |
|---|---|---|
| Execution page tabs | Kestra | Run Inspector tabs |
| Event history timeline | Temporal | Timeline and compact history |
| Parent/child relationships | Temporal | Runtime/sub-workflow graph with member labels |
| Run matrix | Airflow | Recent run health matrix |
| Structured event logs | Dagster | Run Inspector logs; not a wall feed area |
| Node tracing canvas | Dify / n8n | Topology with node status |
| Graph + conversation | LangGraph / LangSmith | Topology plus messages |
| Trace tree | Langfuse / Phoenix | External trace links and span summaries |
| TV/kiosk readability | Grafana / Datadog / New Relic | Big-screen wall layout |

### 9.8 Big-Screen Information Architecture

Default layout:

```text
Top status strip:
  Live status
  running runs
  waiting human
  failed
  avg latency
  projection freshness

Left rail:
  live run window: running, recently completed, priority-pinned published runs
  compact cards
  status, current step, duration, progress

Center:
  workflow step graph from existing GraphCanvas studio variant
  node status overlay: active, waiting, completed, failed
  traversed / focused edges from existing execution trace decoration

```

Primary states:

1. Live.
2. Degraded live stream.
3. Running.
4. Waiting human.
5. Failed.
6. Retrying.
7. Completed.
8. Stale projection.

Multi-run behavior:

1. The wall monitors many published runs at once.
2. The top strip aggregates all wall-visible runs.
3. The Live Run Window lists the highest-priority run cards and may summarize overflow by team/status.
4. The center graph expands only one focus run because multiple full workflow graphs would become unreadable on a large screen.
5. Waiting/failed/retrying/stale issues appear in run-card badges and as focus reasons instead of a separate queue.
6. Timeline and event detail are available only after handoff to Run Inspector in MVP.

Long-workflow behavior:

1. Do not fit all steps into the center graph when the workflow has many steps.
2. Enlarge the current execution window, for example `steps 9-13 of 24`, so every visible step remains readable from a large screen.
3. Add a **Workflow Step Overview** under the enlarged window to show total progress, status colors, and viewport position.
4. Use ELKjs layer-based layout to compute stable directed positions for the workflow step graph before rendering with `GraphCanvas`.
5. Let the wall director move the current window as execution advances or when a higher-priority waiting/failed step appears.
6. Full pan/zoom and node-by-node exploration belong in Run Inspector, not the wall.

Focus run director:

The center graph should act like an automatic director:

1. Failed or timed-out run.
2. Waiting human/input/approval run.
3. Stale live observation or stale projection.
4. Retrying run or recoverable tool failure.
5. Recently updated running run.
6. Recently completed run inside the retention window.

Switching rules:

1. A focus run should stay visible for a minimum dwell time, for example 15-30 seconds.
2. A more severe priority event may interrupt the dwell time.
3. Recently completed runs can be shown briefly, then yield to running/priority runs.
4. The wall should expose a short focus reason, for example `Focused because: waiting for approval 2m`.

### 9.9 Run Inspector / Drilldown Information Architecture

Run Inspector tabs:

1. **Overview**: status, run id, command id, team, member, workflow, implementation kind, state version, freshness, result.
2. **Topology**: selected workflow step graph, plus optional runtime topology.
3. **Timeline/Gantt**: step duration and ordering.
4. **Messages**: conversation and role replies.
5. **Logs**: structured logs, not just raw server logs.
6. **Events**: normalized events and raw event payload for advanced debugging.
7. **State**: typed state snapshot and readmodel freshness.
8. **Outputs**: final output, artifacts, summaries.
9. **Metrics**: latency, retries, tool counts, token/cost if available.
10. **Trace Links**: Jaeger/Phoenix/Langfuse/LangSmith links if configured.

### 9.10 Execution Topology Model

Node types:

| Node | Meaning | Wall Label |
|---|---|---|
| Workflow Step | Existing workflow definition/audit step | Step id/type |
| Role / Target Role | Existing workflow role field | Role/member label when available |
| Tool / Connector Step | Existing `tool_call` / `connector_call` step | Step id + connector/tool label |
| Human Input / Approval Step | Existing `human_input` / `human_approval` step | Step id + waiting/action state |
| Sub-workflow Step | Existing `workflow_call` / dynamic workflow step | Child workflow label |
| Projection / ReadModel Badge | State version/freshness metadata, not a graph node by default | Readmodel freshness |

Edge types:

| Edge | Meaning | Visual |
|---|---|---|
| Command accepted | Request accepted for dispatch | Run card/status badge |
| Step transition | Existing workflow control flow edge | Directional line |
| Branch transition | Existing branch edge | Labeled branch edge |
| Tool call | Existing tool/connector step execution | Step node badge + latency |
| Signal / Resume | Continuation after wait | Yellow edge |
| Projection materialized | Committed fact to readmodel | Freshness badge |
| ReadModel observed | Query/read visibility | Freshness badge |

Statuses:

| Status | Visual |
|---|---|
| Running | Node glow + animated outgoing edge |
| Completed | Stable success color |
| Waiting | Yellow breathing |
| Failed | Red priority highlight |
| Retrying | Loop badge |
| Stale | Grey/dashed |
| Projected | Blue check/badge |

### 9.11 Message-Flow Model

Message flow is not raw network traffic. It is typed workflow movement:

1. Command accepted.
2. Step requested.
3. Step completed.
4. Branch selected.
5. Tool or connector step started/finished.
6. Human input/approval requested/responded.
7. Signal buffered/resumed.
8. Workflow completed/failed/stopped.
9. Projection materialized/readmodel observed.

Each message-flow animation must map to a typed event or observation category. If the durable relation is not known yet, the UI may animate it transiently but must not persist it as fact.

### 9.12 Run Inspector Event Detail Model

The MVP wall does not include an event feed area. Structured events belong in Run Inspector.

Run Inspector can show structured business-readable events:

```text
collect_sources completed · retrieve_facts · 3 sources · 1.8s
Risk Reviewer found 2 issues · waiting approval
ChronoStorage query completed · 42 records
Release Approval waiting for human confirmation
Workflow completed · customer-review · 12.4s
Projection updated · WorkflowRunInsightReport · selected run v42
```

Event detail fields:

| Field | Description |
|---|---|
| timestamp | Display time |
| severity | info/success/warning/error |
| member/role | Who acted |
| action | What happened |
| object | Step/tool/workflow/readmodel |
| summary | Short safe summary |
| duration | Optional latency |
| run id | Hidden/copy in drilldown |

Event detail rules:

1. Show wall-safe summaries when linked from the wall.
2. Hide raw payload by default.
3. Use raw payload only in Run Inspector advanced tabs.
4. Event detail must remain in Run Inspector and stay secondary to the topology and live run cards.

### 9.13 Privacy And Safety Rules

Never show on the wall by default:

1. Raw prompts.
2. Raw model reasoning.
3. Credentials.
4. Authorization headers.
5. Access tokens.
6. Secure input values.
7. Full user-provided documents.
8. Raw `EventEnvelope`.
9. Raw OTel span payload.

Allowed on the wall:

1. Information category.
2. Safe short summary.
3. Tool name.
4. Result count or status.
5. Latency.
6. Team member or role name.
7. Step type.
8. Error class or sanitized message.

Drilldown may reveal more only when permissioned and masked.

### 9.14 Data Sources

Default Aevatar mapping:

| Source | Used For | Surface |
|---|---|---|
| `WorkflowExecutionCurrentState` | status, final output/error, state version | Wall + Inspector |
| `WorkflowRunInsightReport` | report, steps, role replies, timeline, summary | Inspector + wall summaries |
| `WorkflowRunTimeline` | timeline and structured event detail | Inspector only |
| `WorkflowRunGraphArtifact` | topology | Wall + Inspector |
| AGUI/SSE events | live animation and current-run deltas | Wall animation + current run |
| OTel traces | external deep trace and span-level debugging | Inspector trace links |
| Service/team/member readmodels | team/member context and labels | Wall grouping |

Architectural boundary:

1. Readmodels are durable truth.
2. AGUI/SSE/OTel are live observation.
3. Host must not assemble business state from process-local registries.
4. Query path must not replay events.

### 9.15 MVP Scope

MVP screens:

1. `/runtime/mission-wall` or equivalent route.
2. Fullscreen/kiosk mode.
3. Top status strip.
4. Live run window left rail.
5. Central workflow step graph canvas.
6. Focus reason badge in the graph header.
7. Click-through to existing published-run replay, Runs, or MissionControl detail where possible.

MVP data:

1. Running/recent/priority-pinned published runs from current-state/readmodel query.
2. Current run graph from existing workflow definition/audit step graph.
3. Live animation from AGUI/SSE where available.
4. Freshness/state version badge.
5. Run Inspector handoff to timeline/messages/events when detail is needed.

MVP interactions:

1. Select a workflow/run.
2. Select a node.
3. Open Run Inspector.
4. Copy run id / command id from inspector, not wall primary.
5. Fullscreen toggle.

### 9.16 Out Of Scope

1. Full historical analytics.
2. Custom dashboard builder.
3. Workflow editing from the wall.
4. Runtime command/control from the wall.
5. Full trace viewer implementation.
6. Cross-cluster graph correctness beyond current readmodels.
7. License-sensitive UI code reuse from source-available projects.

### 9.17 Success Metrics

Product metrics:

1. 3-second comprehension: 80% of testers can identify live/blocked/failed state.
2. 10-foot readability: key labels and counters readable from meeting-room distance.
3. Incident detection: Live Run Window and focus graph point to the correct failed/waiting run and step.
4. Drilldown handoff: operator reaches relevant timeline/log/event detail within two clicks.
5. Privacy: zero raw secrets/prompts/reasoning shown on wall in review set.

Engineering metrics:

1. Wall state recovers after refresh from readmodels.
2. Live stream disconnect shows degraded state within 5 seconds.
3. Old readmodel versions do not overwrite newer versions.
4. Wall does not introduce process-local workflow/session state registries.
5. Existing architecture guards continue to pass.

### 9.18 Open Questions

1. Is the wall scoped to one Team, one Scope, or global operations by default?
2. What labels should represent "Team" and "Member" for non-technical viewers?
3. Which workflow information categories are safe to show?
4. What dwell time and rotation cadence should the focus director use?
5. Should the wall be authenticated and permissioned separately from Console?
6. Should token/cost metrics appear in MVP or stay drilldown-only?
7. What is the expected screen size and viewing distance?
8. If procurement wants a paid component, what exact gap in React Flow must justify GoJS/yFiles evaluation?

### 9.19 Engineering Stories

#### Story 1: Mission Wall Route

As an operator, I can open a fullscreen mission wall route and see running/recent published runs, topology, and focus state.

Acceptance:

1. Route renders without selecting a run.
2. Route supports fullscreen/kiosk layout.
3. Data source state is visible: live/fresh/degraded.
4. No raw JSON/log payload in primary view.
5. No separate priority list or event feed area is required for MVP.

#### Story 2: Live Run Window

As a viewer, I can see which published runs are currently running, newly completed, or blocked without using a mouse.

Acceptance:

1. Cards show workflow name, team/scope, status, current step or completion age, duration.
2. Failed/waiting/retrying/stale runs sort above healthy runs.
3. Selecting a card updates topology and focus reason.
4. Completed runs remain visible for the configured retention window so short workflows do not vanish from the wall.

#### Story 3: Workflow Step Graph

As a viewer, I can see the selected workflow's real step graph with each step's execution state.

Acceptance:

1. Nodes derive from existing workflow step graph/audit data.
2. Step execution status reuses existing execution decoration patterns where possible.
3. Live animation derives from AGUI/SSE/OTel observation only.
4. Stream disconnect leaves durable graph visible and marks live layer degraded.
5. Node status mapping is consistent with readmodel/audit status.

#### Story 3A: Focus Run Director

As a viewer, I can trust the center graph to show the run most worth understanding without using a mouse.

Acceptance:

1. The director selects from wall-visible published runs only.
2. Failed/timed-out, waiting-human, stale, retrying, running, and recently completed runs are prioritized in that order.
3. Focus switching respects minimum dwell time unless a higher-severity event appears.
4. The center graph displays a short focus reason.
5. Focus selection is deterministic and testable.

#### Story 4: Run Inspector Handoff

As an engineer, I can drill from wall to run inspector with context preserved.

Acceptance:

1. Inspector opens selected run and node.
2. Tabs include timeline, messages, logs, events, state, outputs, metrics, trace links.
3. Run id, command id, member id, runtime id, and state version are copyable in the inspector.

#### Story 5: Privacy Guardrails

As a security reviewer, I can confirm the wall does not expose sensitive data.

Acceptance:

1. Prompts and raw reasoning are not shown by default.
2. Headers/tokens/secure inputs are masked.
3. Tests cover representative sensitive payloads.

### 9.20 Risks And Validation Plan

| Risk | Impact | Mitigation |
|---|---|---|
| Wall becomes a dense debug page | Big-screen failure | Separate Mission Wall and Run Inspector |
| Live observation treated as truth | Architecture violation | Readmodels are durable source; live layer only animates |
| Sensitive data leaks | Security/privacy issue | Summary-only wall, masking, review set |
| Topology becomes unreadable | UX failure | Cluster nodes, selected-run mode, zoom presets |
| Too much motion | Viewer fatigue | Motion only for live flow and focus transition |
| Missing readmodel fields | Fake UI or brittle parsing | Add typed fields/submessages, not generic bags |
| External UI code license risk | Legal risk | Copy patterns, not code |

Validation probes:

1. **3-second comprehension test**: show wall for three seconds; ask if system is healthy, busy, or blocked.
2. **10-foot readability test**: verify labels, counters, and priority states from across a meeting room.
3. **Incident replay test**: replay a failed workflow; run card and focus graph must identify failing run, step, and reason.
4. **Privacy review**: run sample prompts, tokens, secure inputs, and reasoning; wall must not expose them.
5. **Data honesty test**: disconnect live stream; wall marks live layer degraded while durable state remains.
6. **Drilldown handoff test**: select failed node; reach timeline/log/event detail within two clicks.

## 10. MVP Plan

### Phase 0: Benchmark And Design Alignment

Deliverables:

1. This document.
2. One visual wireframe for Mission Wall.
3. One wireframe for Run Inspector drilldown.
4. Data contract matrix.

Decision gate:

1. Confirm wall scope: global, scope, team, or selected workflow.
2. Confirm safe display policy.
3. Confirm whether MVP includes LLM token/cost.

### Phase 1: Mission Wall Skeleton

Scope:

1. Route and fullscreen layout.
2. Static readmodel-backed state.
3. Live run window.
4. Existing `GraphCanvas` studio graph rendered in wall layout.
5. Focus reason badge from deterministic director rules.

Verification:

1. Frontend typecheck.
2. Wall renders from mocked and live data.
3. No raw payload on wall.
4. No separate priority list or event detail area in MVP wall layout.

### Phase 2: Live Message Flow

Scope:

1. AGUI/SSE live events animate topology.
2. Stream state badge: live/degraded/disconnected.
3. Node pulse and edge flow.
4. Focus reason and run-card badges reconcile with readmodel refresh.

Verification:

1. Disconnect/reconnect behavior.
2. Readmodel refresh recovers stable state.
3. Live-only events disappear or reconcile honestly.

### Phase 3: Run Inspector Handoff

Scope:

1. Open inspector from run, node, focus reason, or freshness badge.
2. Reuse existing Runs Timeline/Messages/Events components where appropriate.
3. Add topology context and trace links.

Verification:

1. Context preserved.
2. Copy ids work.
3. Timeline/log/event detail reachable.

### Phase 4: Typed Semantics And Metrics

Scope:

1. Fill missing typed fields for step/tool/latency/output summary.
2. Token/cost if available and safe.
3. Projection freshness and state version display.
4. Better graph grouping.

Verification:

1. Architecture guards.
2. Projection/readmodel guards.
3. Privacy test set.

## 11. Data Contract Matrix

| Wall Need | Existing Source | Gap |
|---|---|---|
| Active run status | `WorkflowExecutionCurrentState` | Need list/filter by team/scope if not already exposed |
| Run summary | `WorkflowRunInsightReport` | Need wall-safe summary fields |
| Step status/duration | report step traces | Need active/current step clarity |
| Role/member activity | role replies + workflow targetRole + Studio member readmodels | Need Team/Member labels from studio readmodels |
| Tool call status | AGUI tool call events / timeline | Need durable tool call summary if historical wall view is required |
| Human waiting | human input/approval events + state | Need typed waiting reason if query is weak |
| Message flow | workflow step graph + execution trace + live observation | Need typed edge/step event semantics for live overlay |
| Projection freshness | state version / updatedAt | Need consistent badge model |
| Trace link | OTel trace context | Need trace id propagation to run/report if not present |

## 12. Implementation Notes For Aevatar

1. Start from existing `GraphCanvas` studio variant and `MemberPublishedRunsReplay` audit-to-graph mapping rather than inventing a second graph component.
2. Add ELKjs as the automatic layout step for workflow graphs with many steps, branches, or long chains. Recommended ELK options for the wall are `elk.algorithm = layered`, left-to-right direction, fixed node dimensions, and explicit edge labels for branch keys.
3. Keep layout as presentation-only data: ELK positions must not become durable workflow facts, and missing layout must fall back to the existing GraphCanvas layout.
4. Use `MissionControl/TopologyCanvas` only when the user explicitly switches to runtime topology or when a run lacks workflow-step audit data.
5. Treat `RunsTracePane` as drilldown, and reuse its Timeline/Messages/Events content for compact wall summaries.
6. Add a wall-specific presentation adapter that maps readmodel/timeline/AGUI events to:
   - `MissionWallRun`
   - `MissionWallNode`
   - `MissionWallEdge`
7. Do not let the adapter own durable state. It maps query results and live events for display.
8. If missing semantics are stable and consumed by production code, add typed proto fields or typed submessages.
9. Keep raw event details inside Run Inspector advanced tabs.

## 13. Recommended Next Artifacts

1. `2026-06-29-workflow-mission-wall-wireframe.md`
2. `2026-06-29-workflow-mission-wall-data-contract.md`
3. `2026-06-29-workflow-mission-wall-mvp-implementation-plan.md`
4. Optional HTML mockup for stakeholder review.

## 14. External References

Workflow orchestration:

1. [Kestra Executions UI](https://kestra.io/docs/ui/executions)
2. [Kestra public repository license](https://github.com/kestra-io/kestra/blob/develop/LICENSE)
3. [Temporal Web UI](https://docs.temporal.io/web-ui)
4. [Temporal UI license](https://github.com/temporalio/ui/blob/main/LICENSE)
5. [Apache Airflow UI overview](https://airflow.apache.org/docs/apache-airflow/stable/ui.html)
6. [Dagster webserver and UI](https://docs.dagster.io/guides/operate/webserver)

AI workflow and agent builders:

1. [LangSmith Studio](https://docs.langchain.com/langsmith/studio)
2. [Dify Run History](https://docs.dify.ai/en/cloud/use-dify/debug/history-and-logs)
3. [Dify license](https://github.com/langgenius/dify/blob/main/LICENSE)
4. [n8n license](https://github.com/n8n-io/n8n/blob/master/LICENSE.md)

LLM observability:

1. [Langfuse Observability](https://langfuse.com/docs/observability/overview)
2. [Arize Phoenix](https://arize.com/docs/phoenix)

Operations wall and dashboard references:

1. [Grafana Canvas](https://grafana.com/docs/grafana/latest/visualizations/panels-visualizations/visualizations/canvas/)
2. [Datadog TV mode](https://docs.datadoghq.com/dashboards/guide/tv_mode/)
3. [New Relic dashboard management](https://docs.newrelic.com/docs/query-your-data/explore-query-data/dashboards/manage-your-dashboard/)

Direct graph component references:

1. [React Flow official site](https://reactflow.dev/)
2. [React Flow / xyflow license](https://github.com/xyflow/xyflow/blob/main/LICENSE)
3. [React Flow ELKjs layout example](https://reactflow.dev/examples/layout/elkjs)
4. [ELKjs repository](https://github.com/kieler/elkjs)
5. [Eclipse ELK layer-based layout documentation](https://eclipse.dev/elk/reference/algorithms/org-eclipse-elk-layered.html)
6. [ELKjs npm package](https://www.npmjs.com/package/elkjs)
7. [AntV X6 license](https://github.com/antvis/x6/blob/master/LICENSE)
8. [GoJS deployment and license keys](https://gojs.net/latest/learn/deployment)
9. [yFiles pricing and licensing](https://www.yfiles.com/pricing.html)
