# Workflow Row Action Hierarchy Design

## Status

Visual direction and the written contract were approved by the user on
2026-08-06. Implementation is authorized.

This specification applies only to the Workflow Activity vNext catalogue row
actions on `feat/2026-08-04_workflow-activity-vnext`. It does not change any
Workflow identity, API, mutation, Activity filtering, or confirmation
semantics.

## Problem

The catalogue currently presents five row capabilities through four adjacent
controls:

- bordered `Open` and `Activity` buttons;
- a standalone red Delete icon for draft rows;
- a neutral overflow trigger containing Rename and Copy workflow reference.

The UI therefore implies that draft deletion deserves more persistent visual
prominence than rename or copy, even though deletion is a low-frequency,
destructive management action. The row also mixes button types without an
explicit action hierarchy and adds capabilities without preserving the
catalogue's original `one clear Open action` contract.

The mismatch is one of placement and prominence: the user expects one obvious
way to enter the Workflow, a secondary way to inspect its Activity, and one
consistent place for lower-frequency management actions.

## Existing Authorities

The implementation must continue to follow:

1. `docs/canon/frontend-design.md` and its component-interaction requirements;
2. `docs/design/2026-04-23-component-interaction-standard.md`;
3. `2026-08-04-workflow-activity-vnext-design.md`, especially the catalogue,
   information-priority, accessibility, and interaction contracts;
4. the existing authoritative Workflow draft and scope APIs.

The global interaction standard governs complete default, hover, active,
disabled, loading, focus, duplicate-submission, and error states. This design
adds the missing catalogue-specific prominence rule; it does not replace the
global standard.

## Chosen Action Model

Each row exposes exactly three persistent controls in this order:

```text
[ Open ] [ View activity ] [ More actions ]
```

### Open

- The only primary action in the row.
- Uses the existing blue primary treatment.
- Includes the edit icon shown in the approved visual and the visible label
  `Open`.
- Is a real link to the row's canonical Workflow editor URL, so modified click,
  middle click, copy link, and browser link semantics continue to work.
- Uses a row-specific accessible name that includes the Workflow name and
  ownership label.

### View Activity

- A neutral secondary action with the same height, radius, typography, and
  icon alignment as Open.
- Uses the visible label `View activity`, not the less specific `Activity`.
- Is a real link containing the row's exact authoritative `workflowId` query.
- Uses a row-specific accessible name that includes the Workflow name and
  ownership label.

### More Actions

- A neutral icon-only overflow trigger with stable dimensions.
- Uses the existing ellipsis icon and a localized row-specific accessible
  name.
- The familiar ellipsis does not require persistent visible copy. It retains a
  visible focus state and the Dropdown's keyboard behavior.
- The trigger remains present even when a published-only row currently has
  only one available low-frequency action. Stable placement is more important
  than changing the row shape by capability count.

## Overflow Menu

Draft-capable rows use this exact order:

```text
Rename
Copy workflow reference
-----------------------
Delete draft
```

Published-only rows show only `Copy workflow reference` until another honest
row capability exists.

- Rename retains the current draft read, update, refresh, duplicate-name
  warning, loading lock, success toast, and error toast behavior.
- Copy retains the exact `workflowId` payload and current success/error toasts.
- Delete moves from the persistent row surface into the menu. It is separated
  from ordinary items and uses the menu's danger presentation.
- Selecting Delete opens the existing confirmation Modal. No deletion occurs
  from the menu click itself.
- The confirmation Modal retains its exact target explanation, pending lock,
  retry behavior, and danger confirmation button.

The menu item hierarchy is semantic, not decorative. Danger red is reserved
for Delete; Rename and Copy remain neutral.

## Visual Contract

- All three persistent controls share one height, radius, icon size, gap, font,
  hover transition, active response, and visible focus treatment.
- Open alone uses primary blue. View activity and More actions are neutral.
- The action cell does not show a standalone red icon.
- The action group has a stable no-wrap desktop layout and cannot resize on
  hover, focus, loading, or menu open.
- Menu items use one row height and icon/text alignment. The divider and danger
  color communicate destructive separation without adding another button
  style.
- Existing Aevatar and Ant Design tokens remain authoritative. No new palette,
  shadow system, radius token, or reusable global component is introduced.
- Persistent buttons reuse `AEVATAR_INTERACTIVE_BUTTON_CLASS` in addition to
  the existing vNext focus and dimension contract.

## Responsive Contract

- Desktop keeps the three-control action group aligned to the end of the row.
- The local table scroll region remains the only horizontal overflow owner.
- At touch-oriented narrow widths, persistent action targets meet the existing
  44 px minimum while preserving the compact desktop target size.
- The overflow menu remains within the viewport and opens from the row's end
  edge.
- No action is discoverable only through hover.

## Accessibility And Navigation

- Open and View activity are links because they navigate; Rename, Copy,
  Delete, and the overflow trigger remain actions.
- Icons accompanying visible labels are decorative. The icon-only overflow
  trigger has a localized accessible name and `aria-expanded`/menu semantics
  through Ant Design Dropdown.
- Duplicate Workflow names remain distinguishable in action names by including
  the authoritative user-facing ownership label.
- The Delete menu item uses both text and danger styling; color is not its only
  signal.
- Keyboard users can reach Open, View activity, More actions, every menu item,
  and the confirmation Modal in DOM order. Closing the menu restores normal
  row navigation without triggering Open.

## Testing Contract

Focused route tests must prove:

1. a row has one primary Open link, one View activity link, and one More actions
   button;
2. no standalone Delete button is present before the menu opens;
3. a draft row menu orders Rename, Copy workflow reference, divider, and Delete
   draft, with Delete marked dangerous;
4. selecting Delete opens the existing confirmation without calling the API;
5. confirming the Modal preserves the existing deletion, refresh, retry, and
   error behavior;
6. Open and View activity expose real canonical `href` values and preserve the
   exact `workflowId` identity boundary;
7. same-name rows receive distinguishable accessible action names;
8. rename and copy behavior continue to pass their existing tests.

Changed source and test files receive focused Jest and changed-file Biome
validation only. Full frontend tests, typecheck, and production build remain
delegated to GitHub CI by the personal frontend workflow policy.

## Non-Goals

- No capability is removed.
- No new Workflow action, bulk action, row selection, Run action, or context
  drawer is added.
- No change to duplicate-name allowance or discriminator content.
- No backend, API, query key, route identity, Activity contract, or toast
  mapping change.
- No redesign of the catalogue table, status badges, search, filters, header,
  navigation, or empty states.

## Expected Implementation Surface

- `workflows/WorkflowsPage.tsx`: link semantics, visible action hierarchy, and
  overflow menu composition.
- `styles.ts`: narrowly scoped stable action layout and touch target rules.
- `index.test.tsx`: observable hierarchy, navigation, menu, and preserved
  mutation behavior.
- Workflow Activity vNext locale catalogues: `View activity` and row-specific
  accessible names.
- The original vNext design: one concise catalogue row-action rule so future
  capabilities cannot return to the persistent row surface by default.

## Acceptance Criteria

- The row visibly presents only Open, View activity, and More actions.
- Open is the only primary button; View activity and More actions are neutral.
- Rename, Copy workflow reference, and Delete draft use one overflow menu.
- Delete draft is last, separated, dangerous, and still requires confirmation.
- All five existing capabilities retain their authoritative behavior.
- Navigation uses real links and remains correct for distinct Workflow IDs.
- The action group is keyboard operable, screen-reader distinguishable, stable
  at desktop density, and usable at touch widths.
