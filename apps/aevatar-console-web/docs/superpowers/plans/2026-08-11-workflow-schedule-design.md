# Workflow Schedule vNext Design Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the Workflow Activity vNext design baseline with an honest
Workflow editor Schedule entry that reuses the existing Team member automation
and `ScheduledDispatch` contracts.

**Architecture:** Keep `ScheduledDispatch` outside the Workflow graph and make
the canonical Team member automation owner (`scopeId + teamId + memberId`) the
Schedule owner. The published service and active revision remain read-only
targets. The PR changes reference documents, the deterministic Excalidraw
generator and its generated board, and the standalone interaction prototype.
It deliberately does not add a runtime route, client API, or backend endpoint.

**Tech Stack:** Markdown, Mermaid, Python 3 Excalidraw generator, static HTML,
CSS, vanilla JavaScript, repository documentation lint.

---

## File Map

- Create: `apps/aevatar-console-web/docs/superpowers/specs/2026-08-11-workflow-schedule-design.md`
- Create: `apps/aevatar-console-web/docs/superpowers/plans/2026-08-11-workflow-schedule-design.md`
- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/aevatar-workflow-activity-vnext.gen.py`
- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/aevatar-workflow-activity-vnext.excalidraw`
- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py`
- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/README.md`
- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/prototype.html`

### Task 1: Lock The Product Contract

**Files:**

- Create: `apps/aevatar-console-web/docs/superpowers/specs/2026-08-11-workflow-schedule-design.md`

- [ ] **Step 1: Record the resource boundary**

Add the exact existing lineage:

```text
Team member Workflow draft -> Publish -> Published Service -> Team member automation -> ScheduledDispatch -> Workflow Run -> Activity
```

State that `workflowId`, `memberId`, and `publishedServiceId` are never
interchangeable, and schedule creation uses the Workflow detail's real
`scopeId`, `teamId`, `memberId`, `activeRevisionId`, and
`publishedServiceId`.

- [ ] **Step 2: Define the UI contract**

Specify `Schedule` beside `Run`, a disabled draft action with `Publish this
workflow before scheduling it.`, a right-side manager panel that mirrors
Team Automation, the recurring-cron form, optional prompt behavior, Dedicated
Agent Key authorization review, pinned revision behavior, `202 Accepted`
observation treatment, and Activity's generic Schedule origin filter.

- [ ] **Step 3: Record the server boundary**

Document the existing member automation routes rooted at:

```text
/api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations
```

Also document the generic `/api/schedules` scheduled dispatch capability as a
lower-level implementation detail. Explicitly prohibit global ownerless
schedule list reads plus browser-side filtering.

### Task 2: Correct The Excalidraw Product Model

**Files:**

- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/aevatar-workflow-activity-vnext.gen.py`

- [ ] **Step 1: Remove Schedule graph nodes**

Replace creation-path step arrays such as:

```python
[("Schedule every Monday", "Schedule"), ("Collect recent feedback", "Lark messages")]
```

with workflow steps that begin at the actual first processing node, for
example:

```python
[("Collect recent feedback", "Lark messages"), ("Group feedback themes", "AI task")]
```

Remove the `Schedule` node appearance branch when no graph node uses it.

- [ ] **Step 2: Add the stable editor action**

Extend `workflow_editor_frame` with a publish-state argument and render the
fixed action order:

```python
button(..., "Run", ...)
button(..., "Schedule", color=MUTED if not published else INK, ...)
button(..., "Add node", ...)
button(..., "Edit YAML", ...)
button(..., "Save", ...)
button(..., "Publish", ...)
```

Use `Publish this workflow before scheduling it.` as visible explanatory text
in draft frames; keep published frames actionable.

- [ ] **Step 3: Draw frame 18**

Add `18 Schedule - published workflow configuration` with the editor canvas
still visible and a right panel showing:

```text
Member automations
Team member / Published service / Pinned revision
Automation name / Cadence / Cron expression / Time zone / Optional prompt
Dedicated Agent Key review
Credential active / Next run / Last run / Server preview
Run now / Pause / Review and reauthorize / Save changes
```

Include a compact failed-dispatch state inside the same frame without claiming
that authorization is missing unless a returned error states it.

- [ ] **Step 4: Update activity and catalogue reference facts**

Show a compact `Scheduled` secondary Workflow summary, preserve generic
Schedule-origin Activity evidence, and make any named schedule detail visibly
conditional on server-returned facts.

### Task 3: Make The Standalone Prototype Match The Baseline

**Files:**

- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/prototype.html`

- [ ] **Step 1: Remove the Schedule node-library entry and mutation path**

Delete the node-library button whose data type is `schedule`, then remove the
matching `addStep` definition:

```js
schedule: makeStep("Schedule", "Schedule", ...)
```

No prototype Workflow document may use `kind: "Schedule"` after the change.

- [ ] **Step 2: Add an editor-level Schedule trigger**

Place an `#editor-schedule` action next to `#editor-run`. Its handler must
open a `.studio-schedule-panel`, not a Run dialog or a node-library modal.

```js
document.querySelector("#editor-schedule").onclick = openSchedulePanel;
```

For draft Workflow documents, render it disabled and set the explanatory title
to `Publish this workflow before scheduling it.`.

- [ ] **Step 3: Render only prototype-owned sample states**

Keep schedule records in a clearly named prototype state object. Render a
right panel with Team member owner, published service, cadence, timezone,
optional prompt, Dedicated Agent Key authorization review, pinned revision,
preview, enabled switch, next and last run, credential status, and a failure
message. Label the prototype state as demonstration data and never describe it
as production API behavior.

### Task 4: Regenerate And Declare The Baseline

**Files:**

- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/aevatar-workflow-activity-vnext.excalidraw`
- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py`
- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/README.md`

- [ ] **Step 1: Generate the deterministic board**

Run:

```bash
python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/aevatar-workflow-activity-vnext.gen.py
```

Expected: a regenerated board with exactly 18 named frames and no escaped
elements.

- [ ] **Step 2: Update the verifier**

Replace the expected frame inventory with the frame-18 entry and replace the
stored SHA with the generated board's SHA. Add assertions for `Schedule`,
`Publish this workflow before scheduling it.`, and the new schedule-panel
title.

- [ ] **Step 3: Update the README**

Add the schedule design supplement to the normative source order, replace
every `17`-frame statement with `18`, add frame 18 to the reading order, and
refine `Run` to mean manual execution only.

- [ ] **Step 4: Review the interaction reference**

Review the standalone prototype's published Workflow and its Schedule panel at
desktop and mobile widths when a browser-accessible target is available. The
local-file browser policy blocks that review in this branch, so the committed
Excalidraw frame remains the durable visual reference; a supplementary PNG is
not a baseline requirement, and no workaround should be used.

### Task 5: Focused Verification And Pull Request

**Files:**

- Verify only the files in this plan's File Map.

- [ ] **Step 1: Run baseline and documentation checks**

```bash
python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py
bash tools/docs/lint.sh
git diff --check origin/feat/2026-08-04_workflow-activity-vnext...HEAD
```

Expected: deterministic baseline, documentation lint, and whitespace check all
pass.

- [ ] **Step 2: Run changed-file frontend analysis**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
```

Expected: documentation/prototype scope is reported; run only any static check
the analyzer names. Do not run a complete frontend suite, typecheck, or build.

- [ ] **Step 3: Review, commit, and create the PR**

Stage only the files in the File Map, commit with:

```bash
git commit -m "Design published workflow schedules"
```

Push `feat/2026-08-11_workflow-schedule-design`, create a Draft PR targeting
`feat/2026-08-04_workflow-activity-vnext`, and include the exact focused
verification commands. State that complete frontend validation is deferred to
GitHub CI.
