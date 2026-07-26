---
title: "Projection Version Regression Repair"
status: approved
owner: eanzhao
---

# Projection Version Regression Repair

## Goal

Restore the production scheduled Agent Key path when a surviving current-state
read model has a higher `StateVersion` than the current authoritative actor event
stream, without allowing lower-version projection writes, treating Elasticsearch
as authority, or requiring an infrastructure operation.

The first supported recovery targets are:

- `StudioWorkspaceGAgent`, whose surviving event stream contains the complete
  current workflow draft;
- `NyxIdAuthorizationCatalogGAgent`, whose current event stream is empty of
  catalog facts and must be rebuilt by a fresh authenticated NyxID observation.

## Confirmed Failure Shape

Production inspection on July 25, 2026 established the following:

- the workspace actor event stream is version `1`, while its Elasticsearch
  current-state document is version `4`;
- the catalog actor event stream is version `2`, while its Elasticsearch
  current-state document is version `20`;
- the workspace version `1` event contains the complete current canary draft,
  while the old version `4` document contains no drafts;
- the catalog versions `1` and `2` are only superseded refresh outcomes, so
  replay produces an empty catalog state;
- the old catalog document contains an expired snapshot and lifecycle fence that
  causes every new refresh begin command to be superseded.

The projection monotonicity guard is correct. The repair must remove only the
exact orphaned replica and then rebuild from an authoritative source.

## Chosen Architecture

### Exact conditional replica deletion

Add a narrow, Elasticsearch-specific repair lease through a separate internal
repair adapter that wraps the ordinary projection store. The ordinary store
does not implement the repair interface, and normal projection-store
registration does not expose the adapter. The repair lease accepts an exact
semantic fingerprint:

- document ID;
- actor ID;
- `StateVersion`;
- `LastEventId`.

The repair lease reads the current document, verifies the full fingerprint,
captures `_index`, `_seq_no`, and `_primary_term`, and deletes from the concrete
index with optimistic concurrency. A changed document returns a typed conflict
and is never deleted. If the delete response is ambiguous because of a
transport failure or timeout, the adapter performs one bounded, exact
reinspection of the same concrete index, document ID, sequence number, and
primary term. It reports `AlreadyAbsent` only when that reinspection proves the
leased revision is absent; otherwise it preserves the failure.

Repair-store registration is explicit and opt-in. It is enabled only for
`StudioWorkspaceCurrentStateDocument` and
`NyxIdAuthorizationCatalogDocument`. The ordinary projection writer contract is
unchanged, the in-memory provider receives no repair capability, and other
Elasticsearch read models cannot resolve this maintenance interface. Catalog
repair command and refresh adapters are likewise separate from the ordinary
Catalog ports and are composed only in the Elasticsearch repair branch.

This capability is not a general reset API. It cannot delete by actor ID alone,
cannot accept a version range, and cannot bypass fingerprint matching.

### Domain-owned repair orchestration

The API host only authorizes, validates, and maps HTTP results. Repair
orchestration remains in Application services behind typed ports.

The Studio repair service depends on:

- a Studio Infrastructure store port that inspects the workspace EventStore
  version and uses the opt-in Elasticsearch repair lease;
- a Studio Infrastructure command port that dispatches a typed workspace
  projection-repair command.

The NyxID catalog repair service depends on:

- a GAgentService Infrastructure store port that inspects the catalog
  EventStore version and uses the opt-in Elasticsearch repair lease;
- the repair-specific authenticated Catalog refresh port and adapter;
- the existing catalog visibility port.

Application owns inspect/delete/recovery ordering. Infrastructure contains only
the EventStore/Elasticsearch/actor-dispatch adapters. Infrastructure may read
`IEventStore.GetVersionAsync` only inside this explicit maintenance path.
Normal query services continue to read only read models.

### Workspace recovery

The workspace repair request is scoped by `scopeId`; callers do not provide a
raw actor ID. The actor ID is derived through `StudioWorkspaceConventions`.

Apply requires the exact source version, replica version, and replica last event
ID returned by dry-run. The Application service re-inspects all values, requires
`replicaVersion > sourceVersion > 0`, conditionally deletes the replica, and
dispatches `RepairStudioWorkspaceProjectionCommand`. After matching the
document fingerprint, the store reads the authoritative EventStore version
again immediately before deletion and rejects any change.

The workspace actor verifies:

- command workspace and scope identities match its current state;
- its current committed EventStore version is at least the inspected source
  version carried as the command's minimum;
- it has an initialized workspace state.

It then calls `RepublishCommittedStateAsync` with a typed workspace event used
only for projection routing. It republishes the actor's actual latest committed
state and version, which can be greater than the inspected minimum. It appends
no domain event and does not change the workspace version.

The HTTP response is `202 Accepted`. Read-model visibility is verified
separately; the command ACK does not claim projection completion.

### NyxID catalog recovery

The catalog repair target is always the elevated caller's own NyxID personal
owner identity. The endpoint does not accept another owner subject, so the
caller's bearer cannot be used to refresh a different user's catalog.

Apply re-inspects the source and replica versions, conditionally deletes the
exact stale catalog document, then invokes the existing authenticated NyxID
catalog refresh through repair-specific command and refresh adapters. After
matching the document fingerprint, the store reads the authoritative EventStore
version again immediately before deletion and rejects any change.

The repair-specific begin command carries the inspected source version as a
minimum. The Catalog actor admits the command only when its actor-owned current
version is at least that minimum and starts refresh with its own authoritative
`State.LifecycleFence`. The repair path does not query the deleted read model to
recover a lifecycle fence or any other fact. It never copies services,
lifecycle fence, freshness, or digest from Elasticsearch into the actor.

The response is:

- `200` when refresh is observed and the rebuilt replica is ready;
- `202` when the refresh committed but replica visibility is pending;
- `409` when the source or replica fingerprint changed;
- `503` when the authenticated refresh fails.

Once either guarded delete returns `Deleted` or `AlreadyAbsent`, authoritative
Workspace republish dispatch and Catalog refresh run independently of the HTTP
request cancellation token. A client disconnect cannot cancel that post-delete
recovery. Catalog visibility lookup may still end with the disconnected
request, so operators must establish completion through the normal read
surfaces described by the runbook.

## Operator API

Expose two platform-admin-only endpoints under the scheduled Agent Key
administrative boundary:

```text
POST /api/admin/scheduled-agent-key/projection-repair/workspace
POST /api/admin/scheduled-agent-key/projection-repair/nyxid-catalog
```

Each request has a single `apply` boolean:

- `apply=false` performs read-only inspection and returns the repair manifest;
- `apply=true` requires the exact expected source version, replica version,
  replica last event ID, and a non-empty repair reason.

Workspace additionally requires `scope_id`. Catalog identity is derived from the
elevated caller.

Bearer tokens are used only for platform-admin authorization and the existing
NyxID refresh call. Tokens, Agent Keys, service credentials, and catalog
contents are never logged or returned.

Unexpected inspection or apply exceptions are mapped to a bodyless, sanitized
HTTP `503`; exception messages, credentials, bearer text, and catalog contents
are not serialized. An `OperationCanceledException` propagates only when the
supplied request cancellation token is actually canceled. Authorization
failures, including an uncanceled authorization exception, remain fail-closed
as `403`.

## Safety and Idempotency

- A healthy document is never deleted.
- A document with another actor ID is always a conflict.
- EventStore version `0` is never repairable.
- Any source-version or fingerprint change between dry-run and apply is a
  conflict.
- Elasticsearch deletion is guarded by `_seq_no` and `_primary_term`.
- The authoritative EventStore version is read again immediately before
  deletion.
- An ambiguous delete outcome is reconciled only by one bounded exact
  reinspection of the leased Elasticsearch revision.
- Retrying after a successful delete but before downstream recovery is allowed:
  a missing replica plus an unchanged expected source version continues with
  workspace republish or catalog refresh.
- Workspace republish uses the deterministic
  `rebuild:{actorId}:{latestVersion}` event ID, republishes a version greater
  than or equal to the inspected minimum, and appends no event.
- Catalog recovery always fetches fresh typed NyxID facts.
- Post-delete Workspace dispatch and Catalog refresh are not canceled by client
  disconnect.
- Downstream endpoint failures return sanitized `503` responses.
- No query path primes, replays, deletes, or rebuilds a projection.
- No generic background scan or startup auto-delete is introduced.

The already-absent continuation remains an operator/audit rule: code can verify
the current actor identity, unchanged positive source version, and strict
`expectedDocumentVersion > expectedSourceVersion`, but it cannot reconstruct
the deleted document's prior fingerprint. A signed inspection token or durable
repair-request-ID record that would make that provenance code-verifiable is
explicitly deferred. This hardening introduces no new secret, configuration
setting, infrastructure operation, or operator step.

## Rejected Alternatives

### Allow lower versions to overwrite higher versions

Rejected because it breaks the repository-wide monotonic current-state contract
and makes delayed committed events capable of rolling back healthy replicas.

### Rehydrate actor state from Elasticsearch

Rejected because current-state read models are query replicas, not authoritative
state. Catalog freshness and authorization evidence must be observed again from
NyxID.

### Automatically delete every regressed document

Rejected because a version regression can indicate unrecoverable authority loss.
Only explicitly supported actor kinds with a known authoritative rebuild source
may use this repair.

### Require a Garnet restore before application repair

Rejected for this incident because the current workspace stream contains the
required draft and the catalog can be rebuilt from NyxID. Infrastructure
recovery remains an independent durability concern, not a dependency of this
feature repair.

## Verification

Automated tests must prove:

- conditional deletion requires the exact actor/version/event fingerprint;
- Elasticsearch uses concrete-index optimistic concurrency deletion;
- a changed document is preserved;
- workspace repair rejects source drift and republishes without appending an
  event;
- catalog repair rejects source drift and rebuilds through a real refresh port,
  never through read-model hydration;
- missing-document retries continue downstream recovery;
- admin endpoints fail closed without an elevated caller;
- catalog repair can target only the elevated caller's own NyxID identity;
- existing projection, Workspace, catalog, architecture, and test-stability
  guards remain green.

Production acceptance is complete only after:

1. dry-run reports the known version regressions;
2. both exact repairs succeed;
3. the hidden workspace draft becomes query-visible and deletable;
4. catalog refresh reports `observed/ready`;
5. scheduled Agent Key preflight succeeds;
6. a temporary scheduled workflow creates a dedicated Agent Key, runs
   `simple_qa`, and revokes the exact key during cleanup.
