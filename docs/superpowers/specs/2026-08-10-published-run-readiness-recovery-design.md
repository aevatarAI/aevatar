# Published Run Readiness Recovery Design

## Context

The Workflow catalogue renders `Published` from the authoritative committed
facts returned by the scope workflow catalogue. Opening that row mounts a new
editor session whose publication receipt starts empty, so Run is disabled until
the user publishes again in that same session. The editor therefore treats a
transient command receipt as durable publication state.

## Semantic Decision

Published Run readiness comes from the scope workflow detail read model for the
exact route `scopeId + workflowId`. A publication receipt remains evidence for
observing a newly accepted Publish command, but it is not the owner of an
already active publication.

`workflowId`, `activeRevisionId`, and `publishedServiceId` retain separate
meanings. The editor may construct a published invocation target only when the
detail is available, its scope and workflow identities match the route, and it
contains non-blank active revision and published service identities.

## Data Flow

1. The editor loads its editable draft through `studioApi.getWorkflow`.
2. In parallel, it reads `/api/scopes/:scopeId/workflows/:workflowId`.
3. An authoritative active publication directly supplies the restored published
   invocation target; the detail read itself is the current read-model evidence.
4. The existing receipt observer remains responsible for a newly accepted
   Publish command until its new active revision and callable service appear.
5. A Publish started in the current editor session temporarily supersedes the
   restored publication until the new receipt is observed.
6. Route changes resolve publication state again for the new exact workflow ID.

The editor records the current local document version when it adopts restored
publication state. Later local edits make that target stale, preserving the
existing save-and-publish-again protection.

## Failure Behavior

An unavailable, mismatched, unpublished, or uncallable workflow detail does not
produce a target. Existing unauthorized, forbidden, delayed, and failed
publication observation states remain unchanged. The editor never substitutes
`workflowId` or `activeRevisionId` for `publishedServiceId`.

## Verification

Focused UI coverage must prove that a published workflow opened in a fresh
editor session enables Run with the exact authoritative service and revision.
Existing coverage must continue to prove that an unpublished workflow remains
disabled, local edits stale the target, and a newly published workflow is not
runnable until observation completes.
