# Workflow publication and Schedule readiness

## Boundaries

This is a frontend-only correctness change. Backend contracts are verified
against `feature/integrate`; that branch is an accepted deployment baseline
and is not required to be merged into `dev` for this change.

Repository-wide CI routing and test sharding are reviewed separately in
optional PR #3697. Frontend PR #3187 targets `dev` directly and does not depend
on that CI PR being merged. Repository CI files remain identical to `dev`.

The visual baseline remains unchanged: Workflow Activity Excalidraw SHA-256
`30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de`
and Schedule Excalidraw SHA-256
`cd5c84c45ffb0cdb253af31b7e1b7616504b35520298f01773026e3b76882d8a`.
The existing design, user-path, Schedule and Schedule History specifications
remain authoritative. Production state comes only from real APIs and
API-acknowledged actions, never prototype data or mock fallback.

## Restoring a publication

A published service and active revision identify an existing publication,
not the version of an independently saved draft. Reopening the editor restores
Run readiness only if the loaded draft YAML matches the published source YAML.
A different or unavailable source leaves Run disabled and Publish available.
Normal edits still invalidate readiness through the editor's document version.

## Streamed execution

Node-start presentation normally waits for two animation frames. It must not
block authoritative stream consumption when browser frames stop arriving.
Abort, document visibility becoming hidden, or the 100 ms presentation budget
all release the wait and clean up outstanding frames and listeners. This
budget does not mark a Run successful or synthesize any execution event.

## Schedule replacement

The Schedule update is a full replacement. The editor preserves the observed
enabled value and headers even though headers do not have a primary form
control. Renaming a paused Schedule neither clears its headers nor enables it.

## Accepted Schedule mutations

Admission is not completion. Accepted feedback is informational; the last
authoritative content stays visible while conflicting mutations remain locked.
Creation observes the Workflow-scoped list. Other actions observe the exact
Schedule detail, with the following completion evidence:

| Action | Required observation |
| --- | --- |
| Create or update | Exact Schedule ID and requested configuration, including headers and enabled state |
| Pause or enable | Requested enabled state |
| Run now | A manual attempt with the receipt's exact `idempotencyKey` |
| Delete | The exact Schedule's `deleted` tombstone or `WORKFLOW_SCHEDULE_NOT_FOUND` |

A different manual attempt, a changed counter, or a matching timestamp cannot
complete Run-now observation. A Workflow-not-found or authorization error
cannot prove Schedule deletion. A manual attempt's observed dispatch outcome
also does not imply that its actual Run finished; Activity owns Run state.

Observation is bounded to 20 seconds per status-check attempt. A timeout or
query error leaves a durable delayed/retry state and does not unlock a duplicate
command. `Check status again` retries only the read, never the accepted mutation.
Closing, unmounting, or switching the owning scope/Workflow clears observation,
cancels the active observation read, and prevents late command receipts from
altering a different surface.

## Verification

Use the dependency preflight and explicit affected Jest paths recorded in the
PR, plus changed-file Biome checks and the test-stability guard. No reliable
affected compiler target exists. GitHub CI owns complete compiler, test,
bundling and canvas benchmark verification. No local backend or mock server is
part of verification.
