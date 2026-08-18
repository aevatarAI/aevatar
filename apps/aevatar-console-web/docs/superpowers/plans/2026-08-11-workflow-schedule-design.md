# Workflow Schedule vNext Design Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align the six-scene Workflow Schedule design with the existing
workflow-scoped backend facade from issue #3446.

**Architecture:** A Schedule is an exact child of
`scopeId + workflowId`. The browser sends workflow-oriented preview and
configuration requests; the backend resolves published service, active
revision, and owner binding. Accepted mutations refresh the same Workflow's
Schedule list or detail before the UI claims final state.

**Tech Stack:** Markdown, deterministic Python Excalidraw generation, Pillow
PNG rendering, static HTML/CSS/JavaScript, and the baseline verifier.

---

## File Map

- Modify:
  `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/aevatar-workflow-schedule-design.gen.py`
- Regenerate:
  `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/aevatar-workflow-schedule-design.excalidraw`
- Modify:
  `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/prototype.html`
- Modify:
  `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/render-schedule-png.py`
- Modify:
  `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py`
- Modify:
  `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/README.md`
- Modify:
  `apps/aevatar-console-web/docs/superpowers/specs/2026-08-11-workflow-schedule-design.md`
- Modify:
  `apps/aevatar-console-web/docs/superpowers/plans/2026-08-11-workflow-schedule-design.md`
- Replace:
  `schedule-authorization-review.png` with `schedule-review.png`
- Regenerate:
  `schedule-workflows-list-modal.png`,
  `schedule-workflow-editor-panel.png`,
  `schedule-creation-pending.png`, `schedule-detail.png`, and
  `schedule-edit.png`

The obsolete combined PNGs and authorization-review PNG must not exist.

## Task 1: Lock The Workflow Resource Contract

- [x] Treat `scopeId + workflowId` as the Schedule collection owner.
- [x] Keep `scheduleId` as the child resource identity.
- [x] Remove Team/Member requirements and visible published-service identity
      from the Schedule flow.
- [x] Keep Team Automation as a separate product resource.
- [x] Keep Schedule independent from Activity and the Workflow graph.

## Task 2: Add The Failing Semantic Contract

- [x] Require the workflow-scoped collection, preview, create, detail, update,
      enable, disable, run-now, and delete routes in
      `verify-baseline.py`.
- [x] Require `displayName`, `cronExpression`, `timezone`, `enabled`,
      and `prompt` request fields.
- [x] Reject Team/Member, generic `/api/schedules`, preflight, credential,
      grant, policy, and reauthorization concepts.
- [x] Run the old baseline and confirm it fails because `Review schedule` is
      missing.

## Task 3: Update The Six Design Scenes

The Schedule source must contain exactly:

```text
01 · Workflows — quick schedule modal
02 · Workflow — schedule setup panel
03 · Schedule — review before creation
04 · Schedule — creation pending
05 · Workflow — schedule detail
06 · Workflow — change schedule
```

- [x] Keep Workflow-list creation in a modal and editor creation in the right
      panel.
- [x] Use the same name, recurrence, timezone, prompt, and enabled fields in
      both containers.
- [x] Replace authorization review with a normal creation review containing
      five server-returned fire times.
- [x] Show `202 Accepted` and Workflow Schedule refresh without optimistic
      Active or next-fire state.
- [x] Limit detail to API-backed Schedule facts and run-now, change,
      enable/disable, and delete actions.
- [x] Make the edit scene visibly preserve the observed enabled state.

## Task 4: Migrate The Interactive Prototype

- [x] Use `configure -> previewing -> review -> accepted` for both entry
      containers.
- [x] Key Schedule fixtures by exact `scopeId + workflowId`.
- [x] Model request objects with the workflow-oriented API fields.
- [x] Model list/detail/update and all colon action routes.
- [x] Preserve `schedule.enabled` in the update request.
- [x] Refresh list/detail after every accepted mutation.
- [x] Keep draft or unpublished Workflows unavailable without falling back to
      Team Automation.

## Task 5: Render And Inspect Standalone PNGs

- [x] Rename the third PNG to `schedule-review.png`.
- [x] Render all six scenes at `1440x900`.
- [x] Keep timezone, Schedule name, recurrence, and preview values consistent
      across scenes.
- [x] Inspect every PNG for blank content, clipping, overlap, and stale
      authorization content.
- [x] Bind each PNG to the current source and renderer hashes.

## Task 6: Focused Verification And Delivery

- [x] Run the system and bundled-Pillow baseline verifier.
- [x] Parse inline JavaScript from both prototype HTML files.
- [x] Run the frontend scope analyzer against
      `origin/feat/2026-08-04_workflow-activity-vnext`.
- [x] Run only analyzer-related Jest and changed-file static checks.
- [x] Run docs lint, Python bytecode compilation, and `git diff --check`.
- [x] Review the full diff and stage only current task files.
- [ ] Commit, push, and update Draft PR #3421 without removing
      `[DO NOT MERGE]`.

The full frontend suite, typecheck, and production build remain delegated to
GitHub CI by the personal frontend validation policy.
