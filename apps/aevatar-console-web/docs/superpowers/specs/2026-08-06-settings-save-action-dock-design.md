# Settings Save Action Dock Design

## Status

Approved and implemented on 2026-08-06.

This specification covers only the Workflow Activity vNext Settings save
action layout. It is based on branch
`feat/2026-08-04_workflow-activity-vnext` at
`3416817a147df0d362bcf770b9cf59f65b624ab8`.

The approved direction is a shell-owned bottom action area. It supersedes the
earlier use of the word `sticky` for this save bar in the Workflow Activity
vNext design specification and user paths. The save contract, authoritative
data source, colors, navigation, and Settings information architecture remain
unchanged.

## Sources And Precedence

This specification must be read with:

1. `docs/canon/frontend-design.md`, including the Workflow Activity vNext
   NyxID visual-alignment rules;
2. `docs/design-baselines/workflow-activity-vnext/README.md` and its primary
   Excalidraw artifact;
3. `2026-08-04-workflow-activity-vnext-design.md`;
4. `2026-08-04-workflow-activity-vnext-user-paths.md`.

This document has precedence only for the location, scroll ownership,
responsive layout, and accessibility semantics of the dirty Settings save
actions. All other contracts retain their existing precedence.

## Problem

The current dirty action bar is rendered inside the AI defaults form and uses
`position: sticky`. Its containing page owns the vertical scroll. As a result:

- the action bar moves with the form before reaching its sticky threshold;
- its location is coupled to the form card rather than the Settings work area;
- below 600 px, CSS changes it to `position: static`, so it is no longer
  persistent at all;
- adding content above or below the form can change when and where it appears;
- a viewport-fixed replacement would need fragile sidebar and gutter offsets
  and could cover the last form field.

The user needs the pending save decision to remain continuously reachable
after editing without covering editable content or moving with that content.

## Goals

- Keep dirty Settings actions stationary at the bottom of the Settings main
  work area on desktop, tablet, and mobile.
- Let the header, local Settings navigation, form, alerts, and technical
  details scroll in a separate region above the actions.
- Keep the action area outside the 200 px desktop navigation rail.
- Preserve the current Aevatar colors and Workflow Activity vNext visual
  language while following the NyxID-derived spacing, density, and responsive
  rules.
- Preserve the existing accepted-to-observed save lifecycle, dirty navigation
  guard, and authoritative API behavior.
- Keep every form field, validation message, and recovery message reachable
  without being covered by the action area.

## Non-Goals

- No backend, API, query, cache, authentication, routing, or persistence
  change.
- No redesign of Preferred service, Default model, Account, or Advanced.
- No new global footer, global navigation behavior, or legacy Settings change.
- No color, typography-family, or brand change.
- No viewport-fixed positioning and no sidebar-width calculation.
- No change to success, failure, delayed-observation, or leave-confirmation
  semantics.

## Chosen Layout

### Shell Footer Slot

`WorkflowActivityVNextShell` receives an optional footer slot. The shell uses
its current layout when the slot is absent. When the slot is present, the main
column becomes two rows:

```text
Workflow Activity vNext shell
  top bar
  navigation rail | main column
                    scroll region
                      page header
                      route content
                    footer region
                      route-provided action dock
```

The main column, not the viewport, establishes the boundary. The scroll region
uses `minmax(0, 1fr)` and owns vertical overflow. The footer occupies its
natural height and never scrolls with the content. This keeps it aligned with
the shell when the desktop rail is visible and when the rail becomes a mobile
drawer.

Routes that do not provide a footer retain the existing main-column scrolling
behavior. The change must not alter Workflows, Activity, Run detail, creation,
or editor layout.

### Settings Ownership

`SettingsPage` always provides the shell footer slot so changing dirty state
does not remount the scrolling form or move focus away from the edited control.
The slot renders no visible content while the draft is clean and renders the
action dock only while it is dirty. The save action component is removed from
the AI defaults form body. It remains owned by `SettingsPage`, so it uses the
existing `draft`, `baseline`, `savePhase`, `discard`, and `save` state and
handlers without introducing a second state container.

The footer wrapper follows the same responsive horizontal gutters as the
Settings content. The dark action surface is centered at the existing Settings
maximum width of 1120 px. It retains the current Aevatar background, border,
text, button colors, radius, and restrained shadow.

## Interaction And State Contract

| State | Dock | Controls | Existing feedback |
| --- | --- | --- | --- |
| Clean | Hidden | None | Quiet current values |
| Dirty | Fixed in shell footer | Restore and Save enabled according to capabilities | Unsaved title and description |
| Saving | Fixed in shell footer | Restore disabled; Save loading and duplicate submission blocked | Existing saving state remains visible |
| Accepted, awaiting observation | Fixed in shell footer | Restore and Save disabled | Existing confirming state remains visible |
| Observed | Removed after baseline matches submitted values | None | Existing success toast |
| Delayed | Fixed because the draft remains dirty | Retry Save or Restore according to current behavior | Existing durable delayed alert |
| Failed | Fixed because the draft remains dirty | Save can be retried; Restore remains available | Existing durable error details |

The dock appears and disappears from actual dirty state only. It does not use
a timer, animation completion, request acceptance, or browser storage as its
authority.

Leaving through local navigation or the browser continues to use the existing
dirty-navigation confirmation and `beforeunload` protection. Moving the
actions must not bypass or duplicate those paths.

## Responsive Contract

### Desktop, At Least 1200 px

- The 200 px navigation rail and 52 px top bar remain unchanged.
- The footer is confined to the flexible main column.
- Its content uses 40 px horizontal gutters and the Settings 1120 px maximum
  width.
- The status copy is on the left and the two actions are on the right.
- The footer consumes layout space; it never overlays the scroll region.

### Tablet, 768-1199 px

- The footer uses the shell's medium horizontal gutter.
- Copy and controls may wrap within the action surface without changing scroll
  ownership.
- Both commands remain visible without horizontal page overflow.

### Mobile, Below 768 px

- The hidden rail and drawer behavior remain unchanged.
- The footer uses 16 px horizontal gutters and
  `env(safe-area-inset-bottom)`.
- Status copy and actions stack when required by available width.
- Actions use stable responsive tracks; at the narrowest supported widths they
  become one column rather than truncating or overlapping.
- The scroll region shrinks to the remaining height, so focused fields,
  validation messages, alerts, and technical details can scroll fully above
  the dock.
- The page must have no horizontal overflow at the 390 x 844 acceptance
  viewport.

## Accessibility

- The footer is an explicitly named region, using localized copy equivalent
  to `Unsaved settings actions`.
- Live save status is scoped to status text rather than placing interactive
  buttons inside a `role="status"` container.
- Restore and Save retain native button semantics, visible focus, disabled and
  loading behavior, and their current accessible names.
- DOM order places the footer after the scroll region, matching its visual
  order and keyboard sequence.
- The fixed layout does not trap focus. When the dock appears, focus remains
  on the field the user edited; no automatic focus jump occurs.
- Reduced-motion preferences remain respected. The footer requires no
  entrance or exit animation.

## Error Handling

This redesign does not create a new error surface. Existing save lifecycle
feedback remains authoritative:

- request failure preserves the dirty draft and technical error details;
- accepted-but-unobserved state remains distinct from saved success;
- delayed observation retains recovery actions;
- only authoritative readback produces the saved success toast and clears the
  dock.

The layout must keep durable alerts inside the scroll region reachable while
the dock remains present. The footer cannot cover or replace those alerts.

## Test Strategy

The highest-value evidence is the existing Workflow Activity vNext route
integration test because the regression involves rendered Settings state,
shell composition, and user actions together.

Add focused coverage that proves:

1. editing AI defaults renders a named save-action region with Restore and
   Save while the clean state does not render that region;
2. the shell exposes the footer as a sibling of its main scroll region, so the
   actions are not children of the form panel;
3. Restore clears dirty state and removes the footer without calling the save
   API;
4. existing Save loading, accepted-to-observed, failure, and dirty-navigation
   tests continue to pass.

CSS is verified through browser QA at 1440 x 900, 834 x 1112, and 390 x 844.
The checks confirm a stationary dock during content scrolling, no covered last
field or alert, no page-level horizontal overflow, reachable buttons, and
correct mobile safe-area spacing.

Local authenticated browser QA was attempted on port 5187 with the real API
proxy and `MOCK=none`. The temporary origin had no authenticated session, and
the configured NyxID client returned to the registered port 5173, which was
already owned by another process. The implementation did not inject a session,
copy browser storage, or substitute mock Settings data. Final authenticated
screenshots therefore remain preview-environment verification; focused route
tests, locale parity, changed-file static checks, and CI protect the local
implementation contract.

Local automated verification remains focused on changed and dependency-related
files. Full frontend tests, typecheck, and production build are delegated to
GitHub CI by the personal frontend workflow policy.

## Affected Files

The expected implementation surface is:

- `src/pages/workflow-activity-vnext/WorkflowActivityVNextShell.tsx` for the
  optional footer slot and scroll-region composition;
- `src/pages/workflow-activity-vnext/settings/SettingsPage.tsx` for providing
  the dirty action dock outside the form;
- `src/pages/workflow-activity-vnext/styles.ts` for main-column, footer,
  responsive, and safe-area layout;
- `src/pages/workflow-activity-vnext/index.test.tsx` for route-level behavior;
- both Workflow Activity vNext locale catalogues for the region label;
- the existing vNext design and user-path documents to replace the superseded
  sticky wording with the shell-fixed action contract.

## Acceptance Criteria

- Dirty save actions remain stationary at the bottom of the Settings main
  column while Settings content scrolls.
- The actions never enter or cover the desktop navigation rail.
- The footer occupies layout space and never covers a field, alert, technical
  detail, or focused control.
- Clean, dirty, saving, accepted, observed, delayed, failed, restore, and
  leave-confirmation behavior preserves the existing authoritative contract.
- Other vNext routes retain their current layout and scroll behavior.
- Desktop, tablet, and mobile have no action overlap or page-level horizontal
  overflow, and mobile accounts for the bottom safe area.
- The action region and controls are keyboard reachable and correctly named.
- Existing Aevatar colors and the NyxID-derived non-color visual rules remain
  intact.

## Required Pull Request Declaration

```text
Design baseline:
  apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/
Primary design:
  aevatar-workflow-activity-vnext.excalidraw
Design SHA-256:
  30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de
Contract specification:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-04-workflow-activity-vnext-design.md
User paths:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-04-workflow-activity-vnext-user-paths.md
Authentication and localization:
  Existing Aevatar login, callback, session, returnTo, and Umi locale logic;
  presentation may change, behavior may not.
Production data source:
  Real APIs and API-acknowledged user actions only; no mock fallback.
Baseline integrity:
  python3 apps/aevatar-console-web/docs/design-baselines/
  workflow-activity-vnext/verify-baseline.py
```
