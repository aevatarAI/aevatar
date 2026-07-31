---
title: "Agent Profile Admin Workbench"
date: 2026-08-01
status: approved
---

# Agent Profile Admin Workbench

## Goal

Turn the existing `#/agent-profiles` Admin surface into a usable Profile workbench. The change preserves the actor-owned Agent Profile model and existing Web API contracts while separating Ornn skill discovery from skill configuration. Users must be able to scan a Profile, add or replace skills without losing form context, understand exact Ornn evidence, and confidently save, validate, and publish.

This is a focused redesign of the existing embedded Backend Console page. It does not create a second frontend application, add a UI dependency, or change the Profile domain lifecycle.

## Ownership And Authorization

- `mine/` remains bound to the authenticated Aevatar scope.
- `system/` remains shared and readable through the existing public summary surface.
- Only an authenticated Aevatar Admin may create or edit `system/` Profiles.
- Selecting, saving, validating, publishing, or binding a Profile retains the existing endpoints, ETag/idempotency rules, and permission checks.
- The workbench never conflates `profileSlug`, Profile identity, Ornn skill GUID, publisher identity, or runtime `agentKind`.

## Visual Direction

Use a restrained operations-workbench aesthetic: dense enough for real administration, quiet neutral surfaces, crisp borders, and status color only where it communicates state. Reuse the Backend Console's existing typography, spacing, color tokens, buttons, badges, modal primitives, and native `details` behavior. Do not introduce generic dashboard card grids, decorative gradients, or a new component library.

The canonical layout remains the existing Profile navigation plus editor workspace. The redesign improves hierarchy inside that workspace instead of changing global navigation.

## Information Architecture

The right-hand workspace has four ordered regions:

1. **Profile identity** — name, owner/slug, purpose, instructions, activation mode, published revision, and availability.
2. **Skills** — count, discovery action, compact collapsible Skill cards, and validation summary.
3. **Advanced policy** — maximum/recovery tool policies and fixed runtime parameters in collapsed native disclosures.
4. **Publish configuration** — personal default actions or Admin-only system rollout controls, depending on owner and authorization.

The current guided creation flow uses the same Skill-card and discovery interaction as ordinary editing. Creation may retain its definition/review steps, but it must not render a second search or member editor implementation.

## Ornn Skill Discovery Modal

### Opening Modes

- `添加 Ornn Skills` in the Skills section opens the modal in multi-select mode.
- `替换 Skill` from an existing card opens the same modal in single-select replacement mode.
- Opening captures the current form into the local working draft before any render, so typed edits are preserved.
- Opening performs no mutation. The search field receives focus.

### Search And Results

The modal is a wide, responsive dialog with:

- a labelled search field and explicit search action; Enter runs the search;
- result count and pagination using the existing `page`, `pageSize`, and `total` list contract;
- loading, empty-query/empty-result, authorization, upstream-error, and retry states;
- selectable result rows with skill name, description, category, tags, visibility, and shortened GUID;
- a clear `已添加` state for a GUID already used by the working Profile; duplicates are disabled;
- a persistent footer with Cancel and `添加 N 个 Skills`, or `替换 Skill` in replacement mode.

The search list does not claim version, publisher, hash, side-effect class, or validation status because the current list contract does not carry those facts. After confirmation, the client resolves each selected GUID through the existing exact endpoint. Only a successful exact read creates or replaces a Skill card.

### Batch Exact Resolution

Multi-select confirmation resolves the selected page's GUIDs concurrently with browser-native `Promise.allSettled`. The existing result page is capped at 20 items, so a custom concurrency queue would add state without changing the practical interaction. The footer reports `正在固定 N 个 Skills` and prevents duplicate submission.

- Successful exact reads are added in the user's result-selection order.
- A generated unique intent ID is derived from the exact skill name using the existing slug-style normalization, with `-2`, `-3`, and so on for collisions. It remains editable in the card.
- Each new member starts with an empty routing description and aliases, `READ_ONLY` side-effect class, and the exact endpoint's declared tools unioned into the member and maximum tool policies using the existing behavior.
- Partial failure keeps the modal open, adds all successful Skills once, leaves failed rows selected with inline reasons and Retry, and changes the footer action to retry only failures.
- Closing during loading cancels the UI request generation; late results are ignored through the existing request token pattern.

Replacement is atomic from the user's perspective: the current card remains unchanged until the replacement exact read succeeds. Its intent, routing description, aliases, and side-effect class are preserved; exact identity, evidence, and declared task tools are replaced/unioned only after success.

## Collapsible Skill Cards

Every Profile member renders as a native `details` Skill card. This is the sole member editor used by creation and editing.

### Collapsed Summary

The summary remains useful without expansion and contains:

- skill name and literal version;
- intent ID;
- side-effect badge;
- publisher;
- evidence/validation status (`待选择`, `Exact 已固定`, `有校验问题`, or published/sealed when present);
- a disclosure affordance with a text label, not color alone.

Cards with incomplete required evidence or matching server diagnostics open by default. Complete cards are collapsed by default. A user's open/closed state is kept in local UI state across ordinary re-renders, indexed by the current working member index. When a new Profile has no Skills, render an intentional empty state with `添加 Ornn Skills`; do not invent a placeholder member merely to keep the form non-empty. Save/Create remains disabled until at least one exact Skill exists.

### Expanded Editor

The expanded body contains:

- editable intent ID and side-effect class;
- routing description and comma-separated trigger aliases;
- a read-only exact evidence block for name, GUID, literal version, publisher, shortened SHA-256 when available, and declared task tools;
- `替换 Skill`;
- manual task tools/tool sets under a nested advanced disclosure;
- `移除 Skill` in a separated danger area.

Removing a card requires confirmation when it contains a selected exact skill. At least one member remains required by the existing Profile validation; an empty Skills section is valid only as unsaved UI state, and Save/Create remains blocked until local validation passes. Removing or replacing a member does not infer which maximum-policy tools are safe to delete.

## Workspace Actions And State

A sticky action bar at the bottom of the workspace keeps the primary lifecycle visible while long Skill lists scroll. It shows one honest state label:

- `未保存修改`
- `正在保存`
- `已接受，等待提交/投影`
- `草稿已保存`
- `校验通过` / `校验失败`
- `已发布 rN`

The action bar contains the existing actions allowed for the current resource: Save draft, Validate, Publish, and explicit default/binding actions. Controls remain disabled during an accepted pending mutation. Validate and Publish continue to reject dirty local state; publication remains complete only when the canonical read model reports `PROFILE_PUBLISHED` with `executionAvailable == true`.

The Profile header retains owner, slug, revision, ETag/state version, and availability, but technical facts use secondary styling. Destructive and rollout actions do not compete visually with Save.

## State, Data Flow, And Failure Handling

All new interaction state stays browser-local and non-authoritative: modal open/mode, query, page, selection, exact-read progress/errors, and card disclosure state. Authoritative Profile facts still come exclusively from the existing actor-backed read models.

The data flow is:

1. Capture fields into the current local draft.
2. Search the existing caller-scoped Ornn list endpoint.
3. Resolve selected GUIDs through the existing exact endpoint.
4. Update the local draft and render cards; mark the draft dirty.
5. Save through the existing draft mutation with ETag and idempotency key.
6. Reconcile the accepted receipt through the existing canonical polling/readback path.
7. Validate and publish through the existing explicit actions.

The modal distinguishes list failures from exact-read failures. Errors stay next to the operation that failed and do not erase successful results or the working Profile draft. A stale ETag, forbidden response, projection delay, or publish failure continues to use the current authoritative error handling.

## Accessibility And Responsive Behavior

- The search surface uses `role=dialog`, `aria-modal=true`, a labelled title, and an `aria-live` result/progress region.
- Opening stores the previous focus; closing restores it. Escape closes when no exact resolution is pending, backdrop click closes, and a minimal focus trap keeps Tab within the dialog.
- Result rows use real checkboxes/radios and labels. Skill cards use native `details/summary`, remain keyboard operable, and expose status as text.
- Focus moves to the first failed field/card after local validation.
- At the existing narrow breakpoint, list and workspace stack, the modal becomes a near-full-height sheet, summaries wrap without hiding primary identity, and the sticky action bar remains reachable above the viewport edge.
- Existing `prefers-reduced-motion` behavior applies; no essential interaction depends on animation.

## Implementation Boundaries

Keep the change in the existing embedded Admin asset and its focused static-asset behavior tests. Reuse current helper functions and modal/details primitives. Extract small named render/state helpers inside the asset only where needed to stop search, card, and lifecycle rendering from remaining entangled; do not create a frontend package or speculative component framework.

No Web API extension is required for this UX: the current list contract honestly supplies searchable summaries and the exact endpoint supplies the authoritative version/publisher/hash/tool evidence. If product requirements later demand version or publisher filtering before selection, that is a separate typed API change.

## Testing And Acceptance

Focused static-asset behavior tests must cover:

- modal multi-select and replacement rendering;
- disabled duplicate results;
- successful batch exact resolution, stable selection order, unique intent IDs, and tool-policy union;
- partial exact-read failure and retry without duplicate members;
- atomic replacement preserving routing fields;
- collapsible summaries, default-open invalid cards, and preserved disclosure state;
- local draft capture across modal renders;
- sticky action-state truthfulness across dirty, pending, saved, validation, and published states;
- Admin-only `system/` edit/create controls and read-only public summary behavior;
- keyboard/Escape/focus-return semantics and responsive CSS hooks;
- existing ETag, idempotency, polling, creation, validation, publication, and canonical route behavior.

Before push, run the focused Capabilities tests, `bash tools/ci/test_stability_guards.sh`, `bash tools/docs/lint.sh`, the relevant architecture guards, full build, and full tests. After deployment, perform signed-in desktop and narrow-viewport browser acceptance on canonical `#/agent-profiles` without mutating unrelated Profiles. Verify search loading/empty/error states, multi-add, replacement, card scanning, save/polling, validation diagnostics, published state, keyboard use, and both personal and Admin-authorized system ownership.

## Non-Goals

- No new Agent Profile, binding, Actor, projection, or Ornn API contract.
- No automatic publish, default binding, side-effect inference, publisher guessing, or removal of policy entries.
- No raw Ornn package/JSON exposure.
- No full-screen creation wizard or second member editor.
- No legacy route restoration, global Backend Console redesign, or separate frontend application.
