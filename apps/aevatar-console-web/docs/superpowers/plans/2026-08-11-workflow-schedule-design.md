# Workflow Schedule vNext Design Implementation Plan

> **For agentic workers:** implement this plan incrementally and keep the
> deterministic Excalidraw, PNGs, prototype, verifier, and documentation in
> sync.

**Goal:** Define a Workflow-owned Schedule experience backed by the existing
Team member automation and `ScheduledDispatch` contracts, with useful
standalone review images and no Activity product dependency.

**Architecture:** The canonical Team member (`scopeId + teamId + memberId`)
owns the automation. `publishedServiceId` and `activeRevisionId` are read-only
target facts. Schedule creation, authorization, pending state, detail, and
editing are presented from Workflows and the Workflow editor. This design does
not add an Activity entry, Activity filter, Schedule-to-Run navigation, runtime
route, client API, or backend endpoint.

**Tech stack:** Markdown, Mermaid, deterministic Python Excalidraw generation,
Pillow PNG rendering, static HTML/CSS/JavaScript, and repository documentation
lint.

---

## File Map

- `apps/aevatar-console-web/docs/superpowers/specs/2026-08-11-workflow-schedule-design.md`
- `apps/aevatar-console-web/docs/superpowers/plans/2026-08-11-workflow-schedule-design.md`
- `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/README.md`
- `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/aevatar-workflow-schedule-design.gen.py`
- `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/aevatar-workflow-schedule-design.excalidraw`
- `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/render-schedule-png.py`
- `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py`
- `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/prototype.html`
- `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/prototype-schedule.html`
- `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/schedule-workflows-list-modal.png`
- `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/schedule-workflow-editor-panel.png`
- `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/schedule-authorization-review.png`
- `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/schedule-creation-pending.png`
- `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/schedule-detail.png`
- `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/schedule-edit.png`

The obsolete `prototype-schedule.png` and
`aevatar-workflow-schedule-design.png` combined overview must not exist.

## Task 1: Lock The Product Boundary

- [x] Keep Schedule ownership on `scopeId + teamId + memberId`.
- [x] Keep `workflowId`, `memberId`, and `publishedServiceId` distinct.
- [x] Use the authoritative `publishedServiceId` and `activeRevisionId` as the
      recurring invocation target.
- [x] Keep Schedule outside the Workflow graph and outside the Run dialog.
- [x] Exclude Activity UI, filters, navigation, and evidence from this
      supplement.

## Task 2: Generate Six Useful UI Scenes

The deterministic Schedule source contains exactly these frames:

```text
01 · Workflows — quick schedule modal
02 · Workflow — schedule setup panel
03 · Schedule — review authorization
04 · Schedule — creation pending
05 · Workflow — schedule detail
06 · Workflow — change schedule
```

- [x] Show the Workflows catalogue behind the quick-create modal.
- [x] Keep the Workflow canvas visible beside the editor Schedule panel.
- [x] Show an editable Schedule name before cadence on both creation surfaces.
- [x] Use `Repeat + time + timezone` as the primary schedule builder.
- [x] Keep raw cron behind `write it as cron instead` and preserve complex cron
      without lossy preset conversion.
- [x] Show only server-returned preview and authorization facts.
- [x] Show `202 Accepted` as pending, not Active, until owner-scoped Schedule
      state is observed.
- [x] Keep detail and edit actions inside the Workflow-owned panel.
- [x] Round-trip the selected Schedule name, cadence, time, timezone, cron, and
      prompt without substituting creation defaults.

## Task 3: Keep The Interactive Prototype Consistent

- [x] Open a `New schedule` modal from published Workflows rows.
- [x] Open the Schedule manager panel from the Workflow editor header.
- [x] Share cadence, preview, authorization, and accepted-state logic between
      the modal and panel containers.
- [x] Remove Schedule-specific Activity navigation and lifecycle copy.
- [x] Never treat an accepted mutation as authoritative state.

## Task 4: Render Standalone PNGs

- [x] Render each of the six frames to its own 1440x900 PNG.
- [x] Do not render a contact sheet, overview board, or generic ambiguously
      named Schedule PNG.
- [x] Bind every PNG to the current Excalidraw source and renderer hashes.
- [x] Reject blank output, wrong dimensions, stale hashes, obsolete PNGs, and
      nondeterministic re-renders in `verify-baseline.py`.

## Task 5: Focused Verification And PR Delivery

- [x] Run the Schedule baseline verifier.
- [x] Run related Jest only for the changed HTML source files.
- [x] Run Biome only for analyzer-reported static-check files.
- [x] Run docs lint, Python bytecode compilation, inline JavaScript syntax
      checking, and `git diff --check`.
- [x] Do not run the full frontend suite, typecheck, or production build; GitHub
      CI owns complete frontend validation.
- [ ] Review the complete diff, stage only task files, commit, push, and update
      Draft PR #3421 while preserving `[DO NOT MERGE]`.
