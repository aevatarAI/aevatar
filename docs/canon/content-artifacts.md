---
title: "Content Artifacts"
status: active
owner: eanzhao
---

# Content Artifacts

A `ContentArtifact` is the durable platform resource for attributable text,
Markdown, structured documents, and other content-oriented execution results.
It preserves exact immutable revisions and provenance while exposing one small,
mutable current-revision pointer. It is not chat history, a workflow input-file
transport, a generic binary step output, or a current-state projection used as
the source of truth.

## Authority And Identity

`ContentArtifactGAgent` is the only authority for one artifact. Its committed
Protobuf events and `ContentArtifactState` own the artifact metadata, access
policy, retention policy, immutable revisions, current pointer, redaction facts,
and tombstone. `ContentArtifactCurrentStateDocument` is an actor-scoped query
replica; it never advances lifecycle or revision state.

The identities remain separate:

| Identity | Meaning |
|---|---|
| `artifactId` | Stable logical identity derived from canonical `scopeId + dedupKey`. |
| actor id | Opaque address derived from Scope and artifact identity. |
| `revisionId` | Stable identity derived from artifact identity and the server-assigned monotonic revision number. |
| concurrency version | Actor-owned compare-and-swap version for mutable commands; it is not a revision number. |
| `runId`, `workflowId`, `publishedServiceId`, `memberId` | Execution provenance identities; none is an artifact identity. |
| `workOrderId` | Optional durable-intent link; it remains a WorkOrder identity. |

Scope ownership is required. Team ownership is optional. When `teamId` is
present, creation validates that the Team exists and is active in the same
Scope. A scope-owned artifact without a Team stores no invented Team identity.
Team ownership records resource context; it grants no implicit artifact access.

## Immutable Labels

An artifact may declare up to eight labels at creation. Labels are immutable
partition facts, not an open metadata bag: keys must match
`[a-z0-9]([a-z0-9._-]{0,62}[a-z0-9])?`, the `aevatar.` prefix is reserved,
and values must be non-empty single-line strings of at most 256 characters.
They participate in the canonical creation request hash. Append, pointer
advance, redaction, expiry, and tombstone never modify them; changing a
partition requires creating an artifact under a new dedup key.

List queries may supply exactly one `labelKey + labelValue` pair for exact
equality. The projection store applies `labels.<key> == value` together with
Scope, ACL, and other filters before cursor paging. Range, full-text, and
multi-label predicates are outside this surface.

## Scope Pin Pointers

`ContentArtifactPinGAgent` is the authority for one mutable pointer identified
by `scopeId + pinKey`. A pin key follows the label-key rules and names a
consumer-defined artifact family such as `daily-ops-report`; it is not the
four-value ContentArtifact kind. Because every mutation for the same key reaches
one actor, set atomically replaces the prior artifact and at most one artifact
is pinned for that key.

Set requires an ACTIVE target in the same Scope owned by the caller. Clear is
authorized from the committed `pinnedBy` fact so a stale or unavailable target
does not prevent explicit cleanup. The actor owns `pinVersion` CAS and
`mutationId` idempotency. Successful set and clear advance `pinVersion`; a CAS
conflict is persisted as a rejected mutation without changing the pointer or
`pinVersion`. The actor current-state read model exposes both authoritative
`pinVersion` and committed projection `stateVersion`.

Artifact lifecycle does not cascade into the pin actor. If a pinned artifact is
later tombstoned or otherwise unavailable, consumers report
`pinned_target_unavailable` and explicitly clear or replace the pointer.

## Immutable Revisions And CAS

Creation commits revision 1 and makes it current. Append assigns the next
revision number from the authoritative revision history. Append never changes a
prior revision's content, hash, provenance, citations, creation time, or
supersession reason. Advancing the current pointer is a separate command.

Append carries no expected concurrency version. Its client-supplied revision
`dedupKey` is the idempotency key: an authorized retry with identical facts is a
no-op, while the same key with different facts fails closed. The Actor assigns
the revision number and id from authoritative state.

Pointer advance, redaction, expiry, and tombstone carry an expected artifact
concurrency version. The Actor authorizes first, then checks CAS before duplicate
or no-op classification. Application read-model version checks are advisory only.
Pointer changes and lifecycle operations may advance the concurrency version
without creating a revision, so neither revision identity nor any other write
fact may be derived from the read model or CAS version.

Each revision contains media type, byte length, SHA-256 content hash, exact
execution provenance, typed citations, and exactly one content location. A
citation identifies either an exact `artifactId + revisionId + contentHash +
mediaType` tuple or a stable external identity. Its locator remains structured
as section, offsets, and selector.

## Content Storage And Integrity

Inline content is bounded to 64 KiB. The Actor hashes and measures inline bytes
before commit. Larger or provider-owned content uses
`IContentArtifactBackingContentPort`; the Actor describes and streams the object,
then verifies both provider descriptor and actual bytes against the revision's
length and SHA-256 hash. Reads stream the object again and verify the exact
committed revision hash before returning content.

The Studio Host provides the `workflow-file` backing adapter:

| ContentArtifact field | Workflow file mapping |
|---|---|
| `backingObject.provider` | Literal `workflow-file`. |
| `backingObject.objectKey` | The stable `FileArtifactRef.ArtifactId`, for example `workflow-file://wf-file-...`; never a filesystem path or credential-bearing URL. |
| revision provenance `scopeId` | Required `FileArtifactRef.OwnerScopeId`. |
| revision provenance `runId` | Exact `FileArtifactRef.OwnerRunId`, or both absent for a non-Run artifact. |

The adapter goes through `IFileArtifactReadPort`; it does not open provider files
or URLs directly. Descriptor ownership must exactly match the revision
provenance. A mismatch, unsupported provider, missing object, invalid descriptor,
or unavailable provider fails closed.

This mapping does not change workflow-file lifecycle semantics. Local/testing
files and production `External` providers retain their configured expiry and
cleanup behavior. If backing content later disappears, the exact revision
metadata and provenance remain, while the content read fails closed as a backing
storage failure. HTTP 410 is reserved for committed redaction, retention expiry,
or tombstone facts. Applications needing retention beyond a workflow file's
lifetime must copy bytes into a backing provider whose retention contract covers
that period and register that provider at the Host boundary.

## Authorization And Lifecycle

The access policy has one typed owner plus explicit reader and writer principal
ids. `principalId` is the ACL identity; `principalKind` is descriptive and never
changes a match. Scope authorization is checked at the HTTP boundary. The
application checks artifact access against the current read model, and the
content query checks the same principal again against the exact snapshot used
for the physical read. Backing access additionally checks Scope and Run
ownership.

The owner can read, append, advance, redact, expire, and tombstone. Explicit
readers can read and discover. A writer-only principal has one append-only
capability: it can append and blindly retry without CAS, but cannot read, list,
advance, redact, expire, attach to a Run, or tombstone. Advance, redaction, and
expiry require owner authority or membership in both reader and writer lists.
Only the owner may tombstone. Redaction and retention expiry clear the content
location while preserving identity, hash, provenance, citations, and typed
reason/time facts. Tombstone clears the current pointer and all surviving
content locations without rewriting historical provenance.

List membership is `owner == caller OR readerPrincipalIds contains caller`. The
projection store applies that ACL together with all user filters before cursor
paging; Host, Application, and query ports do not post-filter materialized pages.

## Run And WorkOrder Interoperability

`ServiceRunGAgent` owns typed result attachments. Its state and current-state
read model carry only `ContentArtifactReference` values containing artifact id,
revision id, content hash, and media type. Full content is never embedded in a
ServiceRun current-state read model. Attachment uses actor-owned CAS through the
narrow `IServiceRunResultArtifactAttachmentPort`; an accepted receipt does not
claim read-model visibility.

WorkOrder references keep their existing contract. A ContentArtifact may be
represented without changing WorkOrder semantics as follows:

| `WorkOrderArtifactReference` field | Mapping |
|---|---|
| `artifact_id` | `ContentArtifactReference.artifact_id`. |
| `artifact_kind` | Literal `content-artifact`. |
| `revision_id` | `ContentArtifactReference.revision_id`. |
| `uri` | Optional same-Scope metadata path `/api/scopes/{scopeId}/content-artifacts/{artifactId}/revisions/{revisionId}`. |

The WorkOrder remains the authority for intent, assignment, approval, and its
declared input/result references. The ContentArtifact remains the authority for
content, revision history, provenance, citations, access, and lifecycle.

## HTTP Surface

The canonical resource root is `/api/scopes/{scopeId}/content-artifacts`.
Endpoints create and list artifacts; read artifact metadata, one exact revision,
the current revision, or verified content; append a revision; advance current;
redact, expire, or tombstone; and attach exact references to a Service Run.

Mutation responses are `202 Accepted` dispatch receipts. Clients observe
committed state through the current-state query surface and its authoritative
`stateVersion`; no endpoint implies query freshness from command acceptance.

The list endpoint accepts an optional paired `labelKey` and `labelValue`.
Pin pointers use `/api/scopes/{scopeId}/content-artifact-pins/{pinKey}` with
GET, PUT, and DELETE; PUT and DELETE return the same accepted-dispatch semantics
as artifact mutations.

Artifact absence and artifact-level ACL denial both return HTTP 404 on reads,
mutations, and Run attachment. A missing revision is also 404. The shared
`scopeId + dedupKey` namespace is intentionally observable only as occupancy:
cross-principal create collision returns HTTP 409 without exposing the occupying
artifact's facts. HTTP 410 is reachable only after read authorization and only
for committed redacted, retention-expired, or tombstoned content. Malformed
requests and readable lifecycle/CAS conflicts remain HTTP 400; missing
authentication and Scope denial remain HTTP 401 and 403.
