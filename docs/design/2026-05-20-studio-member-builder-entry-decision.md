---
title: "Studio Member Builder Entry Decision"
status: DECIDED
date: 2026-05-20
related:
  - "./2026-04-20-studio-member-workbench-information-architecture.md"
  - "./2026-04-20-studio-member-workbench-implementation-checklist.md"
  - "./2026-04-22-team-member-first-prd.md"
---

# Studio Member Builder Entry Decision

## Decision

`Studio` is positioned as the **Team Member Builder**.

It is not a Team Detail `Advanced Edit` tab, and it is not a generic Team
configuration surface. Users enter Studio to create, edit, bind, publish, test,
and observe a specific Team member.

## Product Boundary

`Team Detail` owns Team-level understanding:

- Team identity and lifecycle
- Member roster and Team composition
- Team Activity
- Team Event Topology
- Team-level status and operational signals

`Studio` owns member-level building:

- Create a member inside the current Team
- Edit a member workflow/script implementation
- Bind the member to a service
- Publish and test the member
- Return to Team Detail with the Team context preserved

The user should feel they are building a member of the current Team, not
jumping into an unrelated editor.

## Entry Points

### Empty Team

When a Team has no members, the primary empty-state action is:

`Create first member`

This opens Studio in create-member mode with the current `scopeId` and `teamId`.

### Team Overview

Team Overview may include a compact builder panel:

- `Build this Team`
- `Add member`
- `Continue editing`
- visible summary of current member/build state when available

This panel is a contextual handoff to Studio, not a replacement for Studio.

### Members Tab

Each member row is the primary Studio entry point for existing members.

V1 row actions:

- `Edit in Studio`
- `Build`

Do not add `Test member`, `View runs`, or metrics-style actions to the v1
member row action set. Those can be revisited after Team Activity, run
drilldown, and Team-scoped test semantics are settled.

This keeps the action attached to the object Studio edits: the member.

### Team Header

Team Header should not expose a primary `Advanced Edit` action.

If a generic Studio entry is still useful as a fallback, it may live under a
secondary `More` menu as `Open Studio`, but it must still carry Team context and
should prefer a selected/default member when one is known.

## Route Semantics

Every Team-to-Studio handoff must carry explicit context:

```text
scopeId
teamId
memberId optional
mode=create-member | edit-member
returnTo=/teams/{scopeId}/{teamId}
```

Studio must render that context visibly:

- Team name
- selected member name, if any
- current lifecycle stage
- `Back to Team`

If required context is missing, Studio must show an honest degraded state rather
than silently opening an unrelated generic editor.

## Team Detail Tabs

Do not add a Team Detail `Advanced Edit` tab.

The intended Team Detail tab direction is:

```text
Overview / Members / Activity / Topology
```

Studio is a builder flow launched from Team/member context, not an information
tab inside Team Detail.

## Team Overview Metrics Decision

Team Overview v1 only displays backend-backed facts.

Do not add frontend-computed Team operational metrics such as periodic message
count, success rate, online rate, average response time, Team-level error rate,
or Team-level throughput until the backend provides a Team-scoped operational
summary, Team Activity read model, or equivalent authoritative metric contract.

Allowed v1 facts:

- Team lifecycle
- Team member count from the Team read model
- Team roster and member composition
- current service, revision, and recent run facts when they come from existing
  member/service/runtime APIs
- recent run hints that are clearly labeled as member/service runtime hints,
  not authoritative Team health metrics

Deferred metrics:

- periodic messages
- success rate
- online rate
- average response time
- Team-level health score
- Team-level failed/waiting/throughput rollups

### Follow-up Implementation Scope

When implementation starts, keep Team Detail Overview mostly as-is because it
already shows current service, recent run, last update, Team composition, and
configuration details rather than advanced operational metrics.

The Teams home page should be tightened so it does not present sampled
member-run status as authoritative Team metrics:

- keep the `AI Team` count, because it comes from the Team roster
- remove the `运行正常` and `需要处理` summary cards
- keep per-Team recent member/service run hints only if the copy makes their
  scope explicit
- update Teams home tests that assert the old summary cards

This is a product semantics change, not a backend requirement. Full Team
operational metrics remain blocked on a Team-scoped backend read model/API.

## Rationale

The SaaS Team Workbench mental model is:

1. Understand the Team.
2. Inspect members and activity.
3. Choose the member to build or fix.
4. Enter Studio with that member selected.
5. Return to Team Detail to observe the result.

Calling this `Advanced Edit` makes the action feel like an administrative
escape hatch. Calling it `Build` or `Edit in Studio` keeps the product language
close to the user's task.

## Non-Goals

- Do not make Studio a generic Team settings page.
- Do not make `Advanced Edit` a first-class Team Detail tab.
- Do not let Studio infer Team context only from auth/session fallback when the
  user came from a Team page.
- Do not move Team-level Activity, Topology, or connector ownership into Studio.

## Implementation Notes

Frontend work should add a shared Team-to-Studio route builder so Team Overview,
Members tab, empty state, and future Team Activity drilldowns produce consistent
links.

Tests should cover:

- empty Team CTA links to Studio create-member mode
- member row action links to Studio edit-member mode
- `returnTo` returns to the same Team Detail
- Team Detail does not render an `Advanced Edit` tab
- missing context produces an explicit degraded state
