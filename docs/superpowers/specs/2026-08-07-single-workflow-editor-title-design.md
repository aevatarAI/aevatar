# Single Workflow Editor Title

## Problem

The workflow editor renders the same workflow name twice: once as the page
heading and again as a full-width text input below it. Both surfaces represent
and update the same workflow title, so the layout implies two concepts where
the product has only one.

## Product Decision

The workflow name has one semantic owner and one editing surface: the editor
header. The header title remains the page's primary heading and becomes an
inline editor. The separate workflow-name input in the secondary toolbar is
removed.

This follows the current n8n editor model, where the workflow name appears once
in the editor header and can be edited in place.

## Interaction

- The workflow name is displayed in the page heading at heading scale.
- The heading is an inline text input with no persistent field-like border.
- Clicking or focusing the heading edits the existing workflow title directly.
- Existing disabled and write-locked states continue to prevent editing.
- Workflow-name validation findings focus the header title editor.
- Save status and the Canvas/YAML segmented control remain in the secondary
  toolbar.
- Save, publish, run, validation, and navigation behavior remain unchanged.

## Implementation Scope

- Extend `WorkflowActivityVNextShell` with an optional custom heading node while
  preserving its required string title for navigation context and fallback
  rendering.
- Render the workflow-name input through that heading node in
  `WorkflowEditorPage`.
- Remove the duplicate input from the editor toolbar.
- Update editor-specific styles for the inline heading and the remaining
  toolbar metadata.

No API, workflow identity, persistence, or publication contract changes are
required.

## Verification

- Add a focused UI assertion that a loaded workflow editor exposes exactly one
  `Workflow name` textbox.
- Preserve existing rename, save, validation-focus, and write-lock assertions.
- Run only the workflow activity test file and changed-file static checks.
- Verify the intended editor route at desktop and mobile widths in the browser.
- Delegate the full frontend suite, typecheck, and production build to GitHub
  CI under the personal frontend validation policy.
