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

## Immutable Revisions And CAS

Creation commits revision 1 and makes it current. Append assigns the next
revision number from the authoritative revision history. Append never changes a
prior revision's content, hash, provenance, citations, creation time, or
supersession reason. Advancing the current pointer is a separate command.

Append, pointer advance, redaction, expiry, and tombstone carry the expected
artifact concurrency version. The Actor checks that version immediately before
committing. Pointer changes and lifecycle operations may advance the concurrency
version without creating a revision, so revision numbering must never be derived
from the CAS version.

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
cleanup behavior. If backing content later expires or disappears, the exact
ContentArtifact revision metadata and provenance remain, while the content API
returns an explicit unavailable result. Applications needing retention beyond a
workflow file's lifetime must copy bytes into a backing provider whose retention
contract covers that period and register that provider at the Host boundary.

## Authorization And Lifecycle

The access policy has one typed owner plus explicit reader and writer principal
ids. Scope authorization is checked at the HTTP boundary. The application
service checks artifact access against the current read model, and the content
query checks the same principal again against the exact snapshot used for the
physical read. Backing access additionally checks Scope and Run ownership.

Writers may append, advance, redact, or expire revisions. Only the owner may
tombstone the artifact. Redaction and retention expiry clear the content
location while preserving identity, hash, provenance, citations, and the typed
reason/time facts. Tombstone clears the current pointer and all surviving
content locations without rewriting the historical provenance.

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
