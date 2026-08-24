# Workflow Template Tooltip Design

## Product Decision

The template catalogue should show only facts that distinguish one template from another. The decorative template marker and the repeated `Workflow inputs` fact do not add decision value, so the catalogue removes both. The execution summary remains visible as `Runs N step(s)` and gains an explanatory tooltip for users who need context.

## Shared Tooltip Contract

`AevatarTooltip` becomes the only direct adapter around Ant Design `Tooltip` in console-web source. It accepts the Ant tooltip contract so existing placements and rich content remain compatible, while supplying consistent defaults for hover and keyboard-focus triggers, top placement, a short reveal delay, and a bounded readable content width.

All existing direct Ant Tooltip usages migrate to this adapter. `AevatarHelpTooltip` remains a specialized help-button composition, but uses `AevatarTooltip` internally. A static source test rejects future direct `Tooltip` imports from `antd` outside the adapter.

## Catalogue Layout

The semantic table keeps five columns: Template, Connection, Does, Updated, and the unlabeled action column. Template rows contain name and description without a decorative icon track. The execution summary is focusable as well as hoverable and explains that the count is the number of configured workflow steps the template runs.

## Accessibility And Localization

Tooltips open on hover and focus. The step-count trigger remains ordinary text visually and receives a focus ring only during keyboard navigation. Tooltip copy is localized in the existing vNext English and Chinese catalogues and preserves singular/plural step wording.

## Verification

Component tests cover default and overridden tooltip behavior. A static import guard covers the repository-wide migration. The template browser regression asserts the removed content, five-column table, and hover/focus tooltip. Focused dependency tests, changed-file static checks, test-stability guards, and existing-browser desktop/mobile inspection complete delivery; full frontend validation remains delegated to GitHub CI.
