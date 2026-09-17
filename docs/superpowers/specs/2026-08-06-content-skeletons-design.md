# Console Content Skeletons Design

## Goal

Replace sparse loading copy, spinners, and premature empty states on full-page and primary-list surfaces with reusable skeletons that preserve the geometry of the content being loaded.

## Scope

This change covers initial loading for these primary surfaces:

- Workflows catalog table
- Services catalog table
- Deployments service table
- Primitives connector-card catalog
- Topology traceable-objects table
- Governance policies, bindings, and endpoints tables
- Runtime GAgents kind list and actor-registry list
- Files primary resource tree
- Mission Wall primary stage

Existing structure-matching skeletons in Teams home, Team Members, Team Automations, and Published Runs remain unchanged.

The change excludes detail drawers, editor content, chat conversations and messages, Run Console selectors, Settings forms, button submissions, polling, and background refresh indicators.

## Interaction Model

Page titles, descriptions, filters, and actions that do not depend on loaded data remain visible during initial loading. Only the primary data region becomes a skeleton.

The skeleton appears only while the initial request is pending and no usable data exists. Background refetching keeps the current content visible and continues to use existing lightweight refresh feedback.

Loading, empty, and error are separate states:

1. Pending initial request renders a structure skeleton.
2. Successful empty response renders the existing empty state.
3. Failed request renders the existing error state and retry action.
4. Successful populated response renders the real content.

## Shared Component

Create `src/shared/ui/AevatarContentSkeleton.tsx` with one semantic loading contract and three visual presets:

```ts
export type AevatarContentSkeletonVariant = "canvas" | "list" | "table";

export type AevatarContentSkeletonProps = {
  readonly ariaLabel: string;
  readonly className?: string;
  readonly columnWidths?: readonly (number | string)[];
  readonly listLayout?: "grid" | "stack" | "tree";
  readonly rows?: number;
  readonly style?: React.CSSProperties;
  readonly variant: AevatarContentSkeletonVariant;
};
```

The component uses Ant Design `Skeleton` primitives and theme tokens. Its root renders `role="status"` and `aria-busy="true"`. The visual blocks are `aria-hidden`; the supplied label remains available to assistive technology through visually hidden text.

### Table Preset

The table preset renders a muted header band and three to five stable rows. `columnWidths` controls both header and body tracks so each page can approximate its real table without recreating skeleton markup.

The preset owns horizontal overflow and a minimum content width derived from its tracks. Mobile users see the same horizontal scrolling behavior as the final tables.

### List Preset

The list preset supports three layouts:

- `stack`: repeated inventory rows with a title, secondary line, and action placeholder.
- `grid`: responsive repeated cards for Primitives.
- `tree`: compact indented lines for the Files resource browser.

The preset contains no product copy and does not infer resource semantics.

### Canvas Preset

The canvas preset renders a stable toolbar/header strip, a small group of node-like blocks, and connecting-content placeholders. It accepts `className` and `style` so Mission Wall can use its existing dark CSS variables and full-height stage geometry.

## Page Integration

Workflows, Primitives, Topology, Runtime GAgents, Files, and Mission Wall add explicit initial-loading branches before their existing empty branches.

Services and Deployments continue using `InventoryReadinessState`; its loading branch delegates to the table skeleton and uses the existing `title` as the accessible label. Visible loading title and description are removed from that branch, while error and empty branches remain unchanged.

Governance replaces loading copy in the Ant Design table empty slot with an explicit table skeleton before rendering each table. Existing table and empty-state behavior remains unchanged after the query settles.

No page moves query ownership, changes query keys, or changes API behavior.

## Responsive Behavior

Skeleton containers use the same parent layout and width constraints as the final content. Grid lists collapse through `auto-fit`; tree lists remain compact; table presets scroll horizontally inside the existing content band. Stable row heights and minimum heights prevent request completion from shifting surrounding controls.

Mission Wall keeps its existing desktop and mobile layout rules. Its canvas skeleton is styled within `.mission-wall` and does not introduce a second color system.

## Accessibility

- One status region announces each primary loading surface.
- Skeleton shapes are decorative and hidden from assistive technology.
- Existing headings, filters, and available actions retain their semantics.
- Empty and error messages are not mounted until the query resolves or fails.
- Motion uses the design-system skeleton animation and respects the project's reduced-motion CSS behavior.

## Testing

Shared tests verify every preset, row/column configuration, semantic status contract, and decorative shape behavior.

Page tests use unresolved promises to prove that:

- The correct skeleton is present during initial loading.
- Real empty copy is absent while the request is unresolved.
- Page chrome remains visible.
- Resolving with empty data replaces the skeleton with the existing empty state.
- Resolving with data replaces the skeleton with the real list or canvas.

Only directly related Jest files, changed-file Biome checks, and the repository test-stability guard run locally. Full frontend typechecking, test suite, and production build remain delegated to GitHub CI under the personal frontend validation policy.
