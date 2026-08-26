# Workflow Schedule Management and History Design Specification

**Status:** Approved for implementation

**Extends:**
[Workflow Schedule vNext Design Specification](./2026-08-11-workflow-schedule-design.md)

**Backend authority:** [Issue #3446](https://github.com/aevatarAI/aevatar/issues/3446),
the Workflow-scoped Schedule facade, and the Schedule detail `recentFires`
contract on `feature/integrate`.

## Product Decision

An existing Schedule has one management surface with two sibling views:

```text
Schedule management
  Overview   configuration and current observed state
  History    bounded recent Schedule attempts
```

This surface does not replace Activity. The product owns three related but
different records:

| Surface | Fact it owns | Source |
| --- | --- | --- |
| Schedule Overview | Recurrence configuration and current observed state | Workflow Schedule detail |
| Schedule History | Bounded recent trigger attempts | Schedule detail `recentFires` |
| Activity | Actual observed Workflow Runs and Run detail | Activity read model |

A Schedule attempt can fail before a Workflow Run exists. For that reason,
the UI must call `recentFires` **attempts**, must not promise complete Run
history, and must not derive or guess a Run URL from command, correlation, or
idempotency identifiers. The backend fire record's non-empty `runActorId` is
the only authoritative per-attempt Run destination.

## User Jobs

The management surface supports four explicit jobs without mixing them into
one long form:

1. Understand whether the Schedule is active and when it will try next.
2. Inspect or change its recurrence and optional Run input.
3. Inspect recent Schedule attempts, including failures before Run creation.
4. Continue to Activity when the user needs the resulting Workflow Runs.

Raw backend diagnostics, accepted-command implementation notes, and resource
identifiers are not primary user jobs.

## Information Architecture

### Entry points

The same state model is used in both existing containers:

- Workflows catalogue: `Schedules` opens the Workflow's Schedule management
  modal.
- Workflow editor: `Schedules` opens the right-side Schedule management panel
  without hiding the canvas.

Both containers start on the Schedule collection. Selecting a Schedule opens
its Overview. Neither container navigates through Activity to manage a
Schedule.

### Schedule collection

Each row shows only the facts needed to choose a Schedule:

- Schedule name;
- Enabled or Paused status;
- human-readable recurrence and timezone;
- next scheduled time, or `No upcoming attempt` when paused or unavailable.

The row is the single selection target. Lifecycle and edit buttons do not
repeat on every collection row. `New schedule` remains the primary collection
action.

### Selected Schedule header

The back/close structure and resource context are stable across Overview,
History, and Edit. Every selected-Schedule state uses the same one-line resource
identity:

```text
[Back]  Morning digest · Weekly Feedback Report  [Close]
```

- Back returns to the Schedule collection inside the current container.
- Close exits the entire Schedule management modal or panel.
- The selected Schedule and owning Workflow stay on one line. Long resource
  names truncate rather than increasing the header height, while accessible
  labels and title text retain the full context.
- Switching Overview and History changes only the content below the tab list;
  it never replaces or rewrites the selected-Schedule header.
- The active tab is the sole view label. `Schedule history` is not repeated in
  the resource title.
- There is no footer-level `Back to schedules` action.

Back and Close are separate controls because they produce different state
transitions. Their accessible names are `Back to schedules` and
`Close schedules`.

### Tabs

Overview and History are a two-item tab list immediately below the header.
Overview is selected every time the user newly selects a Schedule. Tab state
may persist while that Schedule stays selected, but must not leak to a
different Schedule.

The tab list supports normal keyboard tab semantics:

- `Tab` focuses the active tab;
- Left/Right move between tabs;
- `Home` and `End` select the first and last tab;
- each tab owns one labelled tab panel.

Edit is not a third tab. It is a temporary mutation mode entered from
Overview and returns to Overview after Cancel or an observed successful
update.

## Overview

### Primary content

Overview is read-only and follows this order:

1. Status and recurrence summary.
2. Next scheduled time.
3. Timezone.
4. Last attempt.
5. Total attempts and failed attempts.
6. Run input, only when non-empty.
7. Collapsed Advanced details containing the raw cron expression.

The human-readable recurrence is primary, for example:

```text
Every weekday at 09:00
```

The raw value `0 9 * * 1-5` is diagnostic configuration and stays under
`Advanced details`. Overview does not repeat a large blue Workflow context
strip or an empty `No prompt` field.

Expanding Advanced details must not require the user to interpret cron syntax.
It presents a compact readable-to-technical hierarchy:

| Detail | Presentation |
| --- | --- |
| Runs | Reuse the same localized human recurrence shown by Overview, such as `Every hour` or `Every weekday at 09:00` |
| Technical format | Show the exact cron expression in monospace for support and debugging |

For a valid expression outside the human builder's supported shapes, `Runs`
honestly says `Custom schedule` and `Technical format` retains the exact value.
Do not add a five-field cron tutorial, duplicate Timezone, or introduce a
second cron interpretation path just for this disclosure.

Dates and times use the current application locale and the Schedule timezone.
They must not use an independent browser-locale formatter that can create a
mixed-language surface.

### Actions

Overview exposes this hierarchy:

```text
[Run now]  [Edit schedule]  [More]
                                Pause / Enable
                                Delete schedule
```

- `Run now` is a direct command. Its accepted response is acknowledged by
  Toast; observed attempt state still comes from Schedule detail.
- `Edit schedule` enters Edit mode with the observed values.
- `Pause` or `Enable` is inside More and reflects the observed current state.
- `Delete schedule` is inside More, visually destructive, and requires a
  confirmation dialog naming the Schedule.

Pause/Enable and Delete must not have equal visual weight with Run now or Edit
schedule. Accepted mutations must not optimistically rewrite authoritative
Schedule state.

### Pending state

When a mutation returns `202 Accepted`, keep the selected Schedule visible,
disable only conflicting mutations, and show a compact inline pending message
or Toast. Refresh the exact Workflow-scoped list/detail until the observed
state changes. Do not show backend implementation narration such as
`Observed Schedule state only` or `Waiting for the Workflow list to confirm`.

## History

### Meaning

The tab title is `History`; its content heading is `Recent attempts`. It shows
the bounded `recentFires` returned by the exact Schedule detail endpoint in
newest-first order.

It is not a lifetime audit log and is not labelled `Runs`, `Run history`, or
`All history`.

### Row model

History uses one compact table or table-like list. Each attempt exposes:

| UI column | Backend source | Presentation |
| --- | --- | --- |
| Scheduled time | `scheduledFireAt` | Application locale in Schedule timezone |
| Source | `manual` | `Manual` when true; otherwise `Scheduled` |
| Schedule outcome | `error` + `runActorId` | `Failed to start`, `Run created`, or `Accepted` according to the mapping below |
| Completed time | `completedAt` | Application locale in Schedule timezone |

The table allocates most horizontal space to the two localized timestamp
columns. `Source` remains compact, and `Schedule outcome` has enough room to
name the observed dispatch state without implying a terminal Run result. The
reference desktop proportions are `28 / 14 / 28 / 30` percent in the column
order above.

Outcome mapping is exact and does not infer execution state:

| Condition | Label | Meaning |
| --- | --- | --- |
| `error` is non-empty | `Failed to start` | The Schedule attempt failed before it could reliably create a Run. |
| `error` is empty and `runActorId` is non-empty | `Run created` | The backend returned an authoritative Run identity; Activity owns its current and terminal state. |
| `error` is empty and `runActorId` is empty | `Accepted` | The attempt has no immediate Schedule error, but this record cannot identify an exact Run. |

A failed row adds one concise message below its main row:

```text
The scheduled attempt could not start the Workflow.
```

For a manual attempt, substitute `manual` for `scheduled`. The raw backend
error is available only through an expandable `Technical details` disclosure
inside that row.

Technical details may show the returned error text for support and debugging.
The primary row must not expose:

- service IDs or endpoint IDs;
- actor IDs;
- command or correlation IDs;
- idempotency keys;
- a guessed Run link when `runActorId` is empty;
- a frontend-invented error category based on raw string matching.

`Completed time` always renders only its localized timestamp. When `runActorId`
is non-empty, one native anchor covers the full data row. The row receives a
single hover and focus treatment, opens the existing Activity Run detail route
in a new tab, and preserves `workflowId + schedule` in the query so Back returns
to the same filtered Activity context. The anchor has a full accessible name
that identifies the attempt and destination, supports keyboard focus and the
browser link context menu, and does not rely on a scripted row click.

Technical details remains an independently interactive disclosure above the
row-link hit area. Any attempt with an empty `runActorId`, whether accepted or
failed, remains non-interactive. It must never guess an identity or fall back to
the Workflow + Schedule filtered Activity list.

### Activity handoff

The History tab provides one secondary link:

```text
View related runs in Activity
```

The link is deliberately about related Runs, not all attempts. It is a native
link that opens a new tab and leaves the current Schedule container and History
state intact. It opens:

```text
/scopes/:scopeId/workflow-activity-vnext/activity
  ?workflowId=:workflowId
  &schedule=:scheduleId
```

Activity owns the visible filter context and Run detail. The target view must
make the Workflow and Schedule filters understandable to the user rather than
silently filtering the table. Schedule is an attribution dimension, not a Run
origin, so the handoff must not add `origin=schedule` or show Schedule in the
Activity Source filter. No backend change is required because the Activity
contract already accepts `workflowId` and `scheduleIds` filters.

The filtered Activity request is an idempotent read. It retries one transient
network, timeout, throttling, or server failure before showing the existing
Activity error state. Authentication, authorization, bad-request, and response
contract errors are not retried. The retry does not remove or weaken the
Workflow and Schedule filters.

## Edit

Edit uses the existing human recurrence builder and preserves the selected
Schedule's observed values:

- name;
- repeat preset plus time, or raw cron mode;
- timezone;
- optional Run input;
- observed enabled state in the full update request.

Raw cron mode and the repeat builder are mutually exclusive. When raw cron is
selected, Repeat and Time are hidden and `Cron expression` is shown; timezone
remains visible. Returning to the builder restores the human controls from a
representable cron value.

`Cancel` discards the draft and returns to Overview. `Save changes` sends the
full replacement, shows accepted feedback, and returns to Overview while the
observed detail refreshes. Edit contains no History table or lifecycle action.

## State Model

```mermaid
stateDiagram-v2
    [*] --> ScheduleList
    ScheduleList --> Overview: Select Schedule
    Overview --> History: Select History tab
    History --> Overview: Select Overview tab
    Overview --> Edit: Edit schedule
    Edit --> Overview: Cancel
    Edit --> Overview: Save accepted
    History --> Activity: Open related runs in new tab
    History --> RunDetail: Open attempt with runActorId in new tab
    Overview --> ScheduleList: Back
    History --> ScheduleList: Back
    Edit --> ScheduleList: Back
    ScheduleList --> [*]: Close
    Overview --> [*]: Close
    History --> [*]: Close
    Edit --> [*]: Close
```

Using Back from Edit abandons the draft and returns to the collection. If the
form is dirty, the container uses the product's normal unsaved-change
confirmation before discarding it.

## Loading, Empty, and Error States

Schedule-detail loading and History loading are explicit states. They do not
render stale collection summaries as if they were detail data.

| State | Required presentation |
| --- | --- |
| Detail loading | Stable skeleton inside the selected-Schedule shell |
| Detail request error | `Schedule couldn't be loaded` with Retry |
| History loading | Stable row skeleton under `Recent attempts` |
| Successful empty History | `No attempts yet` |
| History request error | `History couldn't be loaded` with Retry |
| Failed attempt | Normal History row with `Failed to start` outcome and Technical details |

A failed attempt is business data, not a History-request error. Refresh and
Retry preserve the selected Schedule and active tab.

## Backend Compatibility

The design uses the existing Workflow-facing contract without additions:

```text
GET /api/scopes/{scopeId}/workflows/{workflowId}/schedules
GET /api/scopes/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}
PUT /api/scopes/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}
POST /api/scopes/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}:enable
POST /api/scopes/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}:disable
POST /api/scopes/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}:run-now
DELETE /api/scopes/{scopeId}/workflows/{workflowId}/schedules/{scheduleId}
```

The Schedule detail provides configuration, counters, current timestamps, and
bounded `recentFires`, including an authoritative target Run actor identity when
an attempt created a Run. The frontend adapter accepts the Workflow facade name
`runActorId` and the scheduled-dispatch transport name `targetActorId`, then
normalizes both to the UI's single `runActorId` field. Activity already supplies
the filtered actual-Run surface and Run detail. There is no backend issue
comment to add for this design.

## Review Artifacts

The deterministic Schedule source contains seven standalone `1440x900`
scenes:

1. `schedule-workflows-list-modal.png`
2. `schedule-workflow-editor-panel.png`
3. `schedule-review.png`
4. `schedule-creation-pending.png`
5. `schedule-detail.png` as the Overview state
6. `schedule-history.png` as the History state
7. `schedule-edit.png`

There is no contact sheet and no Schedule-specific Activity frame. Overview
and History are separate PNGs so each state can be reviewed at readable size.

## Acceptance Criteria

- Selecting a Schedule opens Overview, not an editable form.
- Overview and History are sibling tabs under one stable selected-Schedule
  header.
- Back returns to the collection; Close exits the container.
- Overview uses a human recurrence and hides raw cron under Advanced details.
- Expanded Advanced details shows the human recurrence before the raw cron;
  raw syntax is never the user's only local explanation of when it runs.
- Empty optional Run input is omitted.
- Run now and Edit schedule are direct actions; Pause/Enable and Delete live
  under More.
- History renders bounded attempts in a compact list and keeps raw failures
  under Technical details.
- An attempt with a non-empty backend `runActorId` opens that exact Activity
  Run. Every attempt without one remains non-interactive; the row never changes
  scope to the Schedule-wide Activity list or guesses an identity.
- `View related runs in Activity` uses exact Workflow and Schedule filters.
- Activity and Run links open in a new tab without closing or navigating the
  current Schedule surface.
- The same `Schedule · Workflow` title stays on one visual line across Overview
  and History.
- Schedule is absent from Activity Source filters and no handoff sends
  `origin=schedule`.
- A single transient Activity handoff request failure is retried once; 4xx
  contract errors and response decoding errors are not retried.
- History column widths prioritize localized timestamps and do not let the
  Schedule outcome tag dominate common rows.
- History has no Action column. A row with an authoritative `runActorId` is one
  native whole-row link, while a row without one remains non-interactive.
- Schedule outcome labels describe only the observed Schedule dispatch state;
  Workflow Run success or failure remains visible in Activity Run detail.
- Loading, successful empty, request error, and failed-attempt states remain
  distinct.
- All timestamps follow the application locale and Schedule timezone.
- The implementation requires no backend contract change.
