# Workflow List Metadata Design

## Product Mismatch

The Workflow list currently gives draft descriptions, publication revision IDs, ownership labels, and update context the same visual priority as the workflow name. This makes internal scope and revision details look like primary product information, while users need to scan workflow identity, lifecycle status, and recency first.

This is primarily a placement and prominence mismatch. The existing runtime and API semantics are sufficient for the requested UI change:

- `description` is user-authored workflow purpose text.
- `activeRevisionId` determines whether a workflow is published, but the identifier itself is diagnostic detail.
- `directoryLabel` can resolve to a technical scope identifier and is not useful in the primary list.
- `updatedAtUtc` is the authoritative last-update time.
- No authoritative creation time exists in the current list contract.

## Chosen Design

Use a four-column catalogue:

```text
Workflow | Status | Last updated | Actions
```

The Workflow column shows only the workflow name. When a workflow has a non-empty description, its name becomes a keyboard-focusable description trigger. Hovering or focusing that trigger opens a fixed-width popover containing the full description. Workflows without descriptions remain plain text and do not expose an empty trigger or popover.

The description popover uses a 320 px content width with a viewport-safe maximum width and normal wrapping. It is contextual supporting information, not part of the row's default reading order.

The Status column contains one badge:

- `Draft` when there is no active revision.
- `Published` when an active revision exists.

The active revision identifier is not rendered in the primary list. The list does not visibly render ownership or scope labels. A draft's user-facing directory label may remain an internal accessibility discriminator for same-name row actions, but it is not row metadata and must not create another visible label. Workflow and service identifiers remain available only through the existing explicit copy-reference action and navigation contracts.

The Last updated column renders the existing authoritative `updatedAtUtc` value with the established localized formatter. The UI does not add a Created column and does not derive a creation time from update time, file metadata, identifiers, or ordering.

## Responsive And Accessible Behavior

The existing horizontally scrollable table remains the layout owner. Column widths prioritize the workflow name, reserve stable space for status and last update, and keep Actions wide enough for the existing commands. On narrow screens, horizontal scrolling preserves the column relationships instead of collapsing unrelated facts into the Workflow cell.

The description trigger supports both pointer hover and keyboard focus through the Ant Design popover behavior. Its focus treatment is visible, and the popover content wraps long descriptions without resizing the table row. No empty description surface is rendered.

## Data Flow

No API, model, or backend change is required.

1. Draft and committed workflow sources continue to merge by `workflowId`.
2. Draft descriptions continue to populate the merged row when available.
3. `activeRevisionId` is converted to the user-facing status label only.
4. `updatedAtUtc` continues to supply the localized last-update value.
5. `directoryLabel` is not rendered as visible row metadata; when available, it may disambiguate the accessible names of same-name row actions.

Search continues to match workflow name, description, and workflow ID even though descriptions and IDs are not permanently rendered. Sorting continues to use `updatedAtUtc` descending.

## Error And Empty States

Loading, partial-source failure, total failure, retry, empty, and filtered-empty states are unchanged. Missing or invalid update values continue to use the existing `Unavailable` behavior. A missing description simply disables the contextual description popover for that row.

## Verification

Focused page tests will assert that:

- the table exposes Workflow, Status, Last updated, and Actions headers;
- descriptions are absent from the default row content and appear through hover and keyboard focus;
- the description popover has a fixed 320 px width and is not created for empty descriptions;
- Published never exposes the active revision identifier;
- ownership labels and the scope ID are absent from the workflow list;
- the authoritative update value is rendered in the Last updated column;
- no Created column or inferred creation value appears;
- existing row actions, search behavior, and identity routing remain intact.

Local verification remains limited to the Workflow Activity test file and changed-file static checks. The full frontend suite, typecheck, and production build are delegated to GitHub CI by the personal frontend workflow policy.
