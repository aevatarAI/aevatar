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

Add a narrow conditional-delete capability beside the existing projection
document reader/writer abstractions. It accepts an exact semantic fingerprint:

- document ID;
- actor ID;
- `StateVersion`;
- `LastEventId`.

The Elasticsearch implementation reads the current document, verifies the full
fingerprint, captures `_index`, `_seq_no`, and `_primary_term`, and deletes from
the concrete index with optimistic concurrency. A changed document returns a
typed conflict and is never deleted.

The in-memory provider implements the same semantic comparison atomically under
its existing store gate so local composition and tests use the same contract.

This capability is not a general reset API. It cannot delete by actor ID alone,
cannot accept a version range, and cannot bypass fingerprint matching.

### Domain-owned repair orchestration

The API host only authorizes, validates, and maps HTTP results. Repair
orchestration remains in Application services behind typed ports.

The Studio repair service depends on:

- a Studio Infrastructure port that inspects the workspace EventStore version
  and workspace read-model fingerprint;
- the conditional replica delete port;
- a Studio Infrastructure command port that dispatches a typed workspace
  projection-repair command.

The NyxID catalog repair service depends on:

- a GAgentService Infrastructure port that inspects the catalog EventStore
  version and catalog read-model fingerprint;
- the conditional replica delete port;
- the existing authenticated catalog refresh port;
- the existing catalog visibility port.

Infrastructure may read `IEventStore.GetVersionAsync` only inside this explicit
maintenance path. Normal query services continue to read only read models.

### Workspace recovery

The workspace repair request is scoped by `scopeId`; callers do not provide a
raw actor ID. The actor ID is derived through `StudioWorkspaceConventions`.

Apply requires the exact source version, replica version, and replica last event
ID returned by dry-run. The Application service re-inspects all values, requires
`replicaVersion > sourceVersion > 0`, conditionally deletes the replica, and
dispatches `RepairStudioWorkspaceProjectionCommand`.

The workspace actor verifies:

- command workspace and scope identities match its current state;
- its current committed EventStore version still equals the expected source
  version;
- it has an initialized workspace state.

It then calls `RepublishCommittedStateAsync` with a typed workspace event used
only for projection routing. It appends no domain event and does not change the
workspace version.

The HTTP response is `202 Accepted`. Read-model visibility is verified
separately; the command ACK does not claim projection completion.

### NyxID catalog recovery

The catalog repair target is always the elevated caller's own NyxID personal
owner identity. The endpoint does not accept another owner subject, so the
caller's bearer cannot be used to refresh a different user's catalog.

Apply re-inspects the source and replica versions, conditionally deletes the
exact stale catalog document, then invokes the existing authenticated NyxID
catalog refresh.

Once the document is missing, the refresh planner uses lifecycle fence `0`.
That matches the empty current actor state, allowing the actor to commit a new
activation, refresh begin, and observed catalog built from current NyxID facts.
The repair never copies services, lifecycle fence, freshness, or digest from
Elasticsearch into the actor.

The response is:

- `200` when refresh is observed and the rebuilt replica is ready;
- `202` when the refresh committed but replica visibility is pending;
- `409` when the source or replica fingerprint changed;
- `503` when the authenticated refresh fails.

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

## Safety and Idempotency

- A healthy document is never deleted.
- A document with another actor ID is always a conflict.
- Any source-version or fingerprint change between dry-run and apply is a
  conflict.
- Elasticsearch deletion is guarded by `_seq_no` and `_primary_term`.
- Retrying after a successful delete but before downstream recovery is allowed:
  a missing replica plus an unchanged expected source version continues with
  workspace republish or catalog refresh.
- Workspace republish uses the deterministic
  `rebuild:{actorId}:{sourceVersion}` event ID and appends no event.
- Catalog recovery always fetches fresh typed NyxID facts.
- No query path primes, replays, deletes, or rebuilds a projection.
- No generic background scan or startup auto-delete is introduced.

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
