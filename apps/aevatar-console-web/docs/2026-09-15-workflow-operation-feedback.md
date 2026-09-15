# Workflow generation and change feedback

The Workflow Activity vNext creation and catalogue surfaces distinguish a
request being accepted from its completed result.

## Describe

- Generate and open shows generation, validation, and saving as separate
  stages. Real server progress and generated text are available in a bounded,
  expandable Generation details section.
- Generation and validation have a two-minute deadline. Cancel generation or
  Change method stops waiting and prevents that attempt from creating a draft.
  The entered name and description remain available. Leaving the page or scope
  invalidates the pending attempt, including a late successful response.
- Once saving starts, cancellation is no longer offered. Draft creation and
  accepted-save observation retain their existing completion/recovery contract.
- A stream ending without a nonempty completed workflow is an error, including
  an HTTP 200 response containing only a partial YAML document. Only the final
  result is parsed and submitted for draft creation.
- Full generation progress still depends on the backend sending events while
  it works. Backend issue #3631 tracks buffering in the reference backend.

## Archive and delete draft

- The confirmation dialog identifies the selected workflow by name. It blocks
  duplicate submission while the command is pending.
- After acceptance, it automatically checks the exact scoped workflow for up
  to 30 seconds, including time spent waiting for a read request. Archive uses
  the archived catalogue and requires Deactivated; deletion requires the exact
  workflow to be absent from the all-workflows catalogue.
- A delayed result remains unconfirmed. A failed check has its own persistent
  status and technical details. Neither is labelled as a completed deletion.
- Check again only reads the accepted operation's result. Closing stops active
  checks without undoing the submitted request. Reopening the same selected
  operation while the page retains it preserves acceptance and offers another
  check. Selecting a different operation or leaving the page ends that local
  recovery context; a later page visit reloads authoritative catalogue state.
- Only observed completion produces a success toast and refreshes the list.
  Old reads cannot close a newer dialog or report stale success.

## Verification scope

Nine added regression cases cover real progress and cancellation followed by
retry, timeout during validation, generation unmount, archive completion beyond
the former short window, failed deletion-check recovery, a hung read with
close/reopen and stale completion, final validated SSE output, and truncated SSE
rejection. The ninth case verifies that confirmed deletion remains distinct
from a failed subsequent list refresh, with a read-only recovery action. Existing catalogue, creation, observation, and Studio adapter tests
also remain part of the focused validation set.

The design continues the Operational Automation Ledger direction and the
17-frame Excalidraw baseline with SHA-256
`30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de`.
The contract and user-path specifications remain
`docs/superpowers/specs/2026-08-04-workflow-activity-vnext-design.md` and
`docs/superpowers/specs/2026-08-04-workflow-activity-vnext-user-paths.md`.
Remote state comes from real APIs and API-acknowledged actions only.
