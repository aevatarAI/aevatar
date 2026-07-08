---
title: "Current-State Readmodel Disaster-Recovery Rebuild via Committed-State Re-Publication"
status: accepted
owner: eanzhao
---

# Current-State Readmodel Disaster-Recovery Rebuild via Committed-State Re-Publication

## Context

Current-state readmodels (e.g. `ExternalIdentityBindingDocument`) live in a projection
store (Elasticsearch) that is physically separate from the authoritative actor event
store (Garnet). A projection-store reset/reindex can wipe a readmodel row while the
owning actor still holds the authoritative fact.

Once wiped, the row was unrecoverable:

- The committed-fact channel is live-forward-only. The only producer of
  `CommittedStateEventPublished` is `GAgentBase.PublishCommittedDomainEventsAsync`, driven
  by a *new* commit; re-firing a projection activation plan re-attaches the relay but
  replays no history (no replay-on-attach, no rewind token).
- The old "emit a projection-rebuild event" trigger was deliberately removed and reserved
  as a proto tombstone (`ExternalIdentityBindingProjectionRebuildRequestedEvent`), because
  actors must persist only real facts.
- For the binding actor this also produced a deadlock: with the readmodel wiped but the
  actor still bound, a re-auth dispatches `CommitBindingCommand`, which the actor discards
  as idempotent (no event) — so the readmodel never re-materializes even after a full
  browser re-login. The concrete user-visible symptom was HTTP 400 "Authenticated NyxID
  owner binding is required for scope owner schedule auth" on schedule creation, because
  both the create-time pre-check and the fire-time token mint read the wiped readmodel.

NyxID issues the opaque `binding_id` only through the browser OAuth flow and exposes no
surface to re-resolve it, and this repository has no write access to NyxID — so recovery
must come from aevatar's own surviving authoritative state.

## Decision

Rebuild a wiped current-state readmodel by having the owning actor **re-emit its current
committed state through the existing committed-state publication trunk, without appending
a new domain event.**

1. Kernel primitive `GAgentBase<TState>.RepublishCommittedStateAsync(IMessage routingPayload)`
   builds a `CommittedStateEventPublished { StateEvent = { EventData = routingPayload,
   Version = CurrentVersion, EventId = "rebuild:{id}:{version}" }, StateRoot = current state }`
   and runs the same publication-hook + publisher path as a normal commit. It appends
   nothing to the event store. Because a current-state materializer rebuilds a row from the
   `state_root` snapshot alone and projection writes are monotonic covering writes, one
   re-emission rebuilds a wiped row and is an idempotent no-op on a healthy one.
2. `ExternalIdentityBindingGAgent` self-heals on the idempotent `CommitBinding`/`RefreshBinding`
   discard branches (so a re-auth via `/init` or Studio login rebuilds a wiped row), and
   handles a new maintenance command `RebuildBindingProjectionCommand`.
3. An operator-gated endpoint `POST /api/oauth/nyxid-binding/rebuild` (Mainnet host, same
   `IPlatformAdminAuthorizer` gate as the OAuth-client rebuild) dispatches that command for
   headless recovery with no browser round-trip.

## Consequences

- Current-state readmodels are rebuildable from surviving authoritative state — headlessly
  and automatically on the next write — restoring the CQRS invariant that a readmodel is a
  disposable replica, using the single publication trunk (no parallel projection-delivery
  system) and no synthetic fact events.
- `RepublishCommittedStateAsync` re-broadcasts a committed fact to all
  `ObserverAudience.CommittedFacts` consumers of the actor at the current version. It is
  therefore only safe for facts whose consumers are idempotent w.r.t. version. Binding
  events qualify (no audit translator; only the binding projection consumes them).
  Extending this to actor types with audit translators requires idempotent audit
  translation first and is out of scope here.
- The rebuild endpoint is a new operator security surface; it reuses the existing
  fail-closed admin-authorizer pattern.

## Alternatives rejected

- Re-fire the projection activation plan alone — rebuilds nothing (forward-only channel).
- Re-introduce a projection-rebuild event — reverts the deliberate tombstone decision and
  writes a non-fact to the event store.
- Rotate the binding via a fresh OAuth exchange to force an event — orphans NyxID bindings
  and still requires a browser round-trip.
