---
name: stitch-ui-redesign-loop
description: "Runtime Verification Kit UI design mode for continuing the Aevatar console Stitch redesign loop. Use when the user explicitly asks to continue Stitch/UI redesign progress; otherwise treat browser checks, local/remote frontend runs, and visual verification as part of the global codex-refactor-loop merged product."
---

# Stitch UI Redesign Mode

This local skill is no longer a separate automation product. It is a Runtime Verification Kit mode under the global `codex-refactor-loop` merged product.

Use it only for the Stitch-specific design exploration and resumable Aevatar console UI progress already recorded here. Do not let it own repository lifecycle, PR/comment/CI repair, blocked queue semantics, or environment-profile naming.

Boundary rules:

- `5174` remote-backend frontend verification is an environment profile, not a Stitch pipeline.
- Browser screenshots, console checks, and local frontend startup are Runtime Verification Kit capabilities.
- React/Umi implementation, test fixes, commits, pushes, and PR handling belong to Repo Execution Loop.
- Product/domain rules discovered during UI work must be recorded as domain playbook/checklist context, not as a new pipeline.

## Overview

Drive the Aevatar console style redesign as a resumable loop. Each pass must inspect local progress, continue the next unfinished step, preserve user changes, and record enough state for the next heartbeat to resume without guessing.

## Resume Protocol

1. Confirm the workspace is `/Users/abigaildeng/Documents/Playground/aevatar`.
2. Run `git status --short --branch` and keep unrelated user changes intact.
3. Prefer branch `refactor/2026-06-04_console-ui-style-redesign`; if on another branch, switch only when safe.
4. Read `.agents/skills/stitch-ui-redesign-loop/references/progress.md`.
5. Continue the first unchecked item in the progress checklist.
6. Update `progress.md` after each meaningful step, including blockers, URLs, screenshots, commands, and decisions.

## Design Loop

Use the Codex Browser plugin for Stitch work.

1. Open Stitch in the visible Codex browser.
2. Give Stitch a concrete redesign brief for the existing Aevatar console, grounded in the current app structure:
   - Product: operational console for agents, teams, workflows, scopes, deployments, settings, chat, and mission control.
   - Desired feel: precise, work-focused, memorable, dense enough for operators, not a generic AI SaaS landing page.
   - Keep workflows intact; redesign style, layout rhythm, hierarchy, tokens, navigation polish, and page surfaces.
   - Avoid generic purple/blue gradients, oversized marketing hero sections, decorative card piles, and low-density filler.
3. Complete at least three rounds:
   - Round 1: request an initial visual direction and screens.
   - Review: critique hierarchy, density, accessibility, implementation fit, and consistency with the repo.
   - Round 2: ask Stitch to revise based on the critique.
   - Review again using the same criteria.
   - Round 3: ask Stitch for a final refined system and implementation-ready screen guidance.
   - Final review: accept only if it is feasible in the existing React/Umi app and satisfies the frontend design rules in `AGENTS.md`.
4. Capture the final design decisions in `progress.md`: palette, typography, spacing, navigation/layout changes, component treatments, and exact screens to update.

If Stitch requires login or manual user action, leave the browser open, document the blocker in `progress.md`, and report the next concrete action.

## Implementation Loop

After the final Stitch review is accepted:

1. Inspect current frontend conventions before editing:
   - `apps/aevatar-console-web/package.json`
   - `apps/aevatar-console-web/src/global.less`
   - `apps/aevatar-console-web/src/layouts/MainLayout.tsx`
   - Relevant page files under `apps/aevatar-console-web/src/pages/`
2. Establish shared design tokens first, preferably in CSS variables or existing theme/config surfaces.
3. Keep page workflows and route structure intact unless the user explicitly asks for a structural product change.
4. Use existing dependencies and local components before adding new libraries.
5. Make changes in tight vertical slices:
   - global tokens and base surfaces
   - main layout/navigation
   - first-viewport dashboard or primary operational page
   - repeated primitives/cards/tables/forms
   - secondary pages only as needed for consistency
6. Do not touch backend, architecture, proto, or external repositories for this style task.
7. Do not edit or remove unrelated untracked files such as translation caches unless the user asks.

## Verification

Run the narrowest useful checks first, then broaden:

1. `pnpm --dir apps/aevatar-console-web tsc`
2. Relevant frontend tests for modified pages.
3. `pnpm --dir apps/aevatar-console-web build`
4. Start or reuse the local frontend dev server.
5. Verify with Browser screenshots at desktop and mobile widths for the changed surfaces.

Record every command and result in `progress.md`. If a check fails, fix the cause or document the blocker and the next step.

## Completion Criteria

The loop is complete only when:

1. Local `dev` has been updated and the redesign branch exists.
2. Stitch has produced at least three reviewed design rounds, or a documented login/manual blocker prevents continuation.
3. The accepted design is summarized in `progress.md`.
4. The UI has been implemented in `apps/aevatar-console-web`.
5. Typecheck/build/relevant tests and Browser visual verification have been run or blockers are documented.
