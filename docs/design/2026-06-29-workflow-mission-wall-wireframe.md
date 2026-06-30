---
title: "Workflow Mission Wall Wireframe"
status: draft
owner: tbd
last_updated: 2026-06-30
references:
  - "./2026-06-29-workflow-mission-wall-product-package.md"
---

# Workflow Mission Wall Wireframe

## 1. Purpose

This document defines the first large-screen wireframe for the Aevatar Workflow Mission Wall.

The wall is optimized for:

1. Shared large screens.
2. Low interaction.
3. Far-distance readability.
4. Live workflow execution topology.
5. Message-flow visibility.
6. Priority states: failed, waiting, retrying, timeout, stale.

It is not a replacement for the Run Inspector. The wall finds and explains the situation; the inspector investigates the selected run.

Product terminology:

1. Wall labels use Team, Workflow Run, Entry Member, Member / Role, Workflow Step, Tool / Connector Step, Human Gate Step, and Projection / ReadModel freshness.
2. Runtime identifiers such as actor ids are not primary wall labels.
3. Runtime ids may appear in Run Inspector debug/runtime sections after a user drills in.
4. The center graph should reuse the existing workflow step graph/replay shape before introducing a new runtime topology.

Reading model:

1. Top strip answers whether the whole wall is live, busy, blocked, or stale.
2. Live Run Window answers which published runs are currently worth watching.
3. Center graph answers how the current focus run is executing.
4. Focus reason and run-card badges answer why the center graph is showing this run.
5. Timeline, messages, logs, and event detail live in Run Inspector, not on the MVP wall.

## 2. Screen Assumptions

Target display:

| Attribute | Default |
|---|---|
| Primary resolution | 1920 x 1080 |
| Minimum supported desktop | 1440 x 900 |
| Viewing distance | 2-5 meters |
| Interaction model | Kiosk/fullscreen, occasional click |
| Refresh model | Readmodel polling plus live AGUI/SSE deltas |

Readability targets:

1. Top counters: readable at 5 meters.
2. Node labels: readable at 2-3 meters.
3. Focus reason and graph badges: readable at 2-3 meters.
4. Raw JSON: never shown on wall.

## 3. First-Viewport Layout

```text
+------------------------------------------------------------------------------------------------+
| AEVATAR MISSION WALL       Live  Running  Waiting  Failed  Retrying  Avg Latency  Freshness     |
+------------------------------+-----------------------------------------------------------------+
| LIVE RUN WINDOW              | WORKFLOW STEP GRAPH + LIVE EXECUTION                           |
| [Risk Review] running        | Focus: waiting for release approval · 2m                        |
|   Collect evidence           | retrieve_facts -> llm_call -> connector_call                    |
|   8/12 steps · 02:31         |        completed      active        completed                   |
|                              |                 -> human_approval(waiting)                     |
| [Customer Onboarding] wait   |                 -> emit/projected(run v42)                     |
|   Approval gate              | Uses existing @xyflow/react GraphCanvas + run audit overlay     |
|   6/9 steps · 05:12          | Animated edges show live message flow                           |
| [Invoice Classifier] done    | Nodes show durable state from readmodels                        |
|   Completed 18s ago          | Detail timeline/messages/events open in Run Inspector           |
+------------------------------+-----------------------------------------------------------------+
+------------------------------------------------------------------------------------------------+
```

## 4. Layout Regions

### 4.1 Top Status Strip

Purpose:

Show system health and aggregate run state in one scan.

Content:

| Element | Example | Source |
|---|---|---|
| Live state | `Live`, `Degraded`, `Disconnected` | AGUI/SSE connection state |
| Running runs | `12` | current-state query where `completionStatus = running` |
| Waiting human | `2` | run current state, timeline/report summary, or future waiting reason readmodel |
| Failed | `1` | current-state query where `completionStatus = failed/timed_out` |
| Retrying | `3` | timeline/report summary |
| Avg latency | `1.8s` | report metrics |
| Freshness | `2s` | newest relevant readmodel `updatedAt`; selected run version appears in the graph header |

Wireframe:

```text
+--------------------------------------------------------------------------------+
| AEVATAR MISSION WALL  Live ●  Running 12  Waiting 2  Failed 1  Retrying 3  Fresh 2s |
+--------------------------------------------------------------------------------+
```

Rules:

1. Always visible.
2. No wrapping at 1920 width.
3. Degraded live stream changes only the live badge, not durable counters.
4. Failed and waiting states must be visually stronger than completed.

### 4.2 Live Run Window

Purpose:

List wall-visible published runs and let the screen keep short executions visible without requiring a mouse. This rail is not only `active`; it contains running runs, recently completed runs, and priority-pinned failed/waiting runs.

Card content:

| Field | Example |
|---|---|
| workflowName | `Risk Review` |
| teamName | `RiskOps` |
| status | `running`, `completed 18s ago`, `failed pinned` |
| currentStep | `Collect evidence` |
| progress | `8 / 12 steps` |
| duration | `02:31` |
| priority badge | `approval`, `failed`, `timeout` |
| visibility reason | `running`, `recently completed`, `priority pinned` |

Wireframe:

```text
+------------------------------+
| LIVE RUN WINDOW              |
|                              |
| +--------------------------+ |
| | Risk Review        LIVE  | |
| | RiskOps                  | |
| | Collect evidence         | |
| | 8/12 steps        02:31  | |
| +--------------------------+ |
|                              |
| +--------------------------+ |
| | Customer Onboarding WAIT | |
| | Growth Team              | |
| | Approval gate            | |
| | 6/9 steps         05:12  | |
| +--------------------------+ |
|                              |
| +--------------------------+ |
| | Invoice Classifier DONE  | |
| | Finance Ops              | |
| | Completed 18s ago        | |
| | 5/5 steps         00:08  | |
| +--------------------------+ |
+------------------------------+
```

Sorting:

1. Failed/timed out and waiting human runs pinned by priority severity.
2. Running runs.
3. Retrying runs.
4. Recently completed runs inside the wall retention window.
5. Older completed runs leave the wall and remain available in history/Run Inspector.

Visibility rules:

1. `running` published runs remain visible while `completionStatus = running`.
2. Completed runs remain visible for a short retention window, for example 3-5 minutes, so fast workflows are still seen on the wall.
3. Failed, timed-out, or waiting-human runs remain pinned longer, for example 15-30 minutes or until acknowledged in the inspector.
4. Published workflow definitions with no recent or running run do not appear here.

### 4.3 Workflow Step Graph And Live Execution

Purpose:

Make the selected workflow's real step graph visible, then overlay execution state and live movement.

The center graph is a focus surface, not the only workflow on the wall. When several workflow runs are active, the wall director chooses one focus run to expand while the surrounding regions continue to show the rest of the live run window.

Long-workflow rule:

When a workflow has many steps, the wall must not shrink the whole graph until every node becomes unreadable. The default large-screen pattern is **current execution window + Workflow Step Overview**:

1. Enlarge the current 4-7 relevant steps in the center graph.
2. Show total step count, for example `Workflow steps 9-13 of 24`.
3. Show a bottom **Workflow Step Overview** where each step has status color.
4. Draw a viewport marker over the overview so viewers know which part is enlarged.
5. Use ELKjs layer-based layout to compute the directed step graph positions before rendering through GraphCanvas.
6. Use Run Inspector for full pan/zoom exploration.

Canvas model:

```text
GraphCanvas studio variant
  layout: ELKjs layered layout for directed workflow graph coordinates
  nodes: workflow steps
  edges: next / branch edges
  node overlay: idle / active / waiting / completed / failed
  focused step: latest or selected run trace item
  long workflow: current execution window + Workflow Step Overview
  freshness: selected run state version badge
```

Wireframe:

```text
   +-------------------+       +-------------------+       +-------------------+
   | retrieve_facts    | ----> | llm_call          | ----> | connector_call    |
   | completed         |       | active            |       | completed         |
   | role: researcher  |       | role: reviewer    |       | ChronoStorage     |
   +-------------------+       +-------------------+       +-------------------+
                \                                           |
                 \                                          v
                  \                              +----------------------+
                   +---------------------------> | human_approval       |
                                                 | waiting              |
                                                 | release_gate         |
                                                 +----------------------+
                                                             |
                                                             v
                                                 +----------------------+
                                                 | emit / projected     |
                                                 | selected run v42     |
                                                 +----------------------+
```

Node statuses:

| Status | Visual |
|---|---|
| Running | active step badge / focused edge |
| Completed | green check |
| Waiting | amber breathing |
| Failed | red outline and priority hint |
| Retrying | loop badge |
| Stale | grey/dashed |
| Projected | blue projection badge |

Edge statuses:

| Edge | Visual |
|---|---|
| Step transition | existing next edge |
| Branch transition | existing branch edge with label |
| Tool call | step node badge with latency/result summary |
| Signal/resume | amber directional pulse |
| Projection | blue freshness badge, not necessarily a graph edge |
| Stale observation | grey dotted edge |

Focus selection:

1. Failed/timed-out run.
2. Waiting human/input/approval run.
3. Stale live observation or stale projection.
4. Retrying run or recoverable tool failure.
5. Recently updated running run.
6. Recently completed run inside the retention window.

Director rules:

1. Keep the current focus visible for a minimum dwell time, for example 15-30 seconds.
2. Allow higher-severity priority events to interrupt immediately.
3. Rotate normal running/recently completed runs when no priority event exists.
4. Show a short focus reason in the graph header or subtitle.

## 5. Run Inspector Handoff

From any wall element:

| Wall Element | Inspector Context |
|---|---|
| Live run card | Selected run overview |
| Topology node | Selected node + topology tab |
| Focus reason badge | Timeline focused at failure/wait event |
| Freshness badge | State/readmodel tab |

Inspector tab order:

```text
Overview | Topology | Timeline | Messages | Logs | Events | State | Outputs | Metrics | Trace Links
```

## 6. Visual Direction

Tone:

1. Operational, not marketing.
2. High contrast, not dark-blue-only.
3. Calm default state, strong priority states.
4. Motion is meaningful, not decorative.

Suggested palette:

| Token | Value | Usage |
|---|---|---|
| canvas background | `#08110F` | deep green-black base |
| panel background | `#10171A` | cards and rails |
| primary live | `#2DD4BF` | live flow |
| success | `#7DDC83` | completed |
| warning | `#F2B84B` | waiting/approval |
| error | `#EF5B5B` | failed |
| projection | `#60A5FA` | readmodel/projection |
| text primary | `#F4F7F5` | major labels |
| text muted | `#A8B3AE` | secondary labels |

Typography:

1. Large counters: 36-48px.
2. Panel headers: 14-16px uppercase.
3. Node labels: 16-20px.
4. Focus reason badge: 13-15px.

## 7. Responsive Behavior

1920 x 1080:

1. Full layout.
2. Left rail 320px.
3. Center graph uses remaining width.
4. Top strip 72px.

1440 x 900:

1. Reduce side rails.
2. Hide secondary metrics.
3. Topology keeps priority.

Below 1200px:

1. Switch to operator desktop mode.
2. Side rails become drawers.
3. This is not the primary big-screen target.

## 8. First Prototype Requirements

Prototype should show:

1. Top status strip.
2. Live run window.
3. Central topology with nodes and message-flow edges.
4. Focus reason badge.
5. Static sample data.
6. A large-screen visual style distinct from the normal Console UI.

Prototype should not show:

1. Raw JSON.
2. Dense tables.
3. Full prompts.
4. Debug-only tabs on the wall.

## 9. Review Checklist

1. Can a viewer identify live/failed/waiting within 3 seconds?
2. Can labels be read from 2-5 meters?
3. Is topology the visual center?
4. Does the wall distinguish durable state from live observation?
5. Are sensitive payloads absent?
6. Is there a clear path into Run Inspector?
