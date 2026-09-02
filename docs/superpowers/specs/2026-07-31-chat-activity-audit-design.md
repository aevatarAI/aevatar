---
title: "Chat Activity Audit"
status: accepted
owner: eanzhao
date: 2026-07-31
---

# Chat Activity Audit

## 1. Product decision

Add a Chat Activity surface under /admin for the Mainnet POST /api/chat
facade. It shows only tool executions and NyxID browser actions. It does not
show prompts, assistant text, reasoning, tool arguments, tool results, uploaded
content, or conversation transcripts.

The feature is operational observability, not compliance-grade zero-loss
audit. Capture failures remain observable operational failures but do not block
chat execution.

Authenticated users may read only activity attributed to their own NyxID
subject. Aevatar platform admins may read activity across all users and scopes.
The default retention period is 30 days.

## 2. Scope

### 2.1 Included

- Tool executions initiated through either branch of Mainnet POST /api/chat:
  NyxID Assistant and Workflow Chat.
- NyxID Assistant browser-action requests, including service.connect.
- Terminal browser-action outcomes:
  - verified completion;
  - declined;
  - failed;
  - cancelled;
  - expired.
- Cursor-paginated personal and platform-admin queries.
- A Chat Activity entry in /admin that reuses the existing audit presentation
  patterns.
- A 30-day Elasticsearch retention operation owned by deployment operations.

### 2.2 Excluded

- Full chat history or transcript search.
- Prompt, response, reasoning, input-part, attachment, tool-argument, or
  tool-result storage.
- Raw NyxID subject, email, display name, token, credential, header, cookie,
  OAuth code, device code, or secret-bearing URL storage.
- Auditing every POST /api/chat HTTP attempt. Tool and action facts are the
  requested product surface; boundary request logs would add noise and storage
  without answering the user need.
- Historical backfill.
- User-name resolution in the activity list. V1 displays the existing
  HMAC-derived audit actor identity to platform admins.
- Compliance-grade durable capture, legal hold, or indefinite retention.
- A ChatLog table, a ChatActivity actor, a second event envelope, or a second
  projection pipeline.

## 3. Existing authority and reusable infrastructure

The implementation reuses these existing authorities:

| Concern | Existing authority |
|---|---|
| Tool execution fact | ToolExecutionAuditMiddleware and AgentToolReceipt |
| Browser action requested | NyxIdChatActionRequestedEvent |
| Browser action report | NyxIdChatContinuationAdmissionCommittedEvent |
| Browser action postcondition | NyxIdChatOperationReconciledEvent |
| Audit artifact contract | AuditRecord protobuf |
| Audit storage and query | IAuditTrailAppender, IAuditTrailQueryPort, audit-trail-current Elasticsearch alias |
| Cross-scope admin decision | IPlatformAdminAuthorizer |
| Audit identity | IAuditActorIdentityHasher and AuditCanonicalActorKeys |
| Action materialization path | StudioMaterializationContext for NyxIdChatConversationGAgent on the unified Projection Pipeline |

Audit artifacts remain governance artifacts. They do not become conversation
state, action authority, or command-decision input.

## 4. Durable contracts

### 4.1 Chat provenance

Add a typed AuditChatProvenance submessage to AuditExecutionProvenance. It has
the following closed fields:

| Field | Meaning |
|---|---|
| surface | NYXID_ASSISTANT or WORKFLOW_CHAT |
| conversation_id | Conversation identity when the producer owns one |
| turn_id | One chat turn/session identity |
| task_id | Actor-owned task identity when available |
| step_id | Actor-owned step identity when available |
| action_request_id | Browser-action identity when applicable |

Missing identifiers remain absent. Producers must not infer them from string
prefixes, route positions, prompts, tool names, or result content.

The same fields are carried to tool execution through a typed
AgentChatInvocationContext on AgentToolExecutionContext. They are not placed in
ExternalMetadata. NyxID Assistant fills conversation and turn at ingress and
fills task/step only where the exact actor operation key is available. Workflow
Chat fills its existing run/session identities without pretending a workflow
run is a NyxID conversation.

Workflow Chat user attribution comes only from the trusted
WorkflowCallerCredential.NyxIdAuthority.ExternalUserId produced at the Host
boundary. WorkflowCallerCredentialToolContextMapper maps that value into
Caller.OwnerSubject before WorkflowRunScopeToolContextMapper applies scope. The
existing scope fallback in WorkflowRunScopeToolContextMapper is not user
identity and cannot populate AgentChatInvocationContext or qualify a record for
Chat Activity.

### 4.2 Conversation owner

NyxID Assistant browser actions need durable user attribution before the
action.continue request exists. Add owner_subject as a typed, actor-owned fact
to new NyxIdChat conversation state. The transport does not add a duplicate
field: conversation creation reads the already-typed
FirstTurn.ToolContext.Caller.OwnerSubject.

Rules:

- Host derives owner_subject from the authenticated principal; callers cannot
  submit it.
- A new public /api/chat conversation commits the owner during creation and
  rejects a conflicting owner on later turns. An ownerless conversation is not
  claimable by a later turn.
- Raw owner_subject never enters AuditRecord, AGUI, transcript, public current
  state, logs, or error text.
- The action audit materializer converts owner_subject to
  AuditCanonicalActorKeys.ForNyxIdUser(owner_subject), hashes it through
  IAuditActorIdentityHasher, and stores only AuditActorId plus IdentityKeyId.
- Existing ownerless conversations are not guessed, adopted from route shape,
  or reconstructed by replay. Their old action facts are absent from personal
  Chat Activity. This is the explicit no-backfill boundary.

This owner field is a resource-ownership fact, not audit metadata. It therefore
belongs to the authoritative conversation actor rather than a query-time join
or process-local map.

### 4.3 Tool records

Keep one final audit artifact per executed tool call. Reuse the existing tool
audit middleware and enrich only its typed chat provenance.

Stored fields are limited to:

- occurred and recorded timestamps;
- HMAC audit actor identity and scope;
- tool name;
- safe downstream target kind and ID from the provider receipt;
- call ID and safe correlation IDs;
- receipt status, outcome, approval mode, destructive flag, and side-effect
  kind;
- stable sanitized failure code;
- typed chat provenance.

The existing redaction contract continues to omit model.prompt,
tool.arguments, and tool.result.

AgentToolReceiptStatus.AuthorizationRequired is a terminal unsuccessful tool
execution with a stable authorization-required failure code and
external-effect not applied. The later browser-action record represents the
new pending action. The tool record must not imply that the action itself has
already been requested or completed.

### 4.4 Action records

Each browser action creates at most two audit artifacts:

1. chat.action.requested
   - Source: NyxIdChatActionRequestedEvent.
   - Phase: accepted, nonterminal.
   - Meaning: the actor committed the action request before AGUI exposed the
     browser card.
2. chat.action.resolved
   - Source: the committed continuation or postcondition fact.
   - Phase: terminal for declined, failed, cancelled, expired, or verified
     resolution.

Resolution mapping:

| Actor fact | Audit result |
|---|---|
| completed and typed postcondition verified | succeeded |
| declined | cancelled with declined code |
| failed | failed |
| cancelled | cancelled |
| expired | timed_out |

A caller-reported completed disposition is never written as success by itself.
Only the actor-committed typed postcondition may produce succeeded. A failed or
unavailable postcondition is not a terminal action fact: the action remains
pending and the activity surface continues to show only its requested record.
This preserves the two-record ceiling and allows a later authoritative recheck
to succeed without contradicting an earlier terminal audit record.

Action artifacts retain action kind, advisory risk, remember eligibility, and
the safe correlation identities listed in AuditChatProvenance. They omit the
entire params message, safe-resource report payload, raw subject, and raw
postcondition response. Resource identities are omitted in V1 because the
requested product surface needs action kind and outcome, not the connected
resource identifier.

Audit IDs are deterministic from the committed event ID, event kind, and
action_request_id. Projection redelivery is therefore idempotent.

## 5. Capture flow

### 5.1 Tool execution

1. The /api/chat adapter builds typed chat provenance from trusted command
   identities.
2. The provenance travels through AgentToolExecutionContext.
3. ToolExecutionAuditMiddleware observes the final typed receipt.
4. ToolAuditRecordFactory creates one sanitized AuditRecord.
5. IAuditTrailAppender writes the record to the existing artifact store.

The middleware remains best effort. An append failure is logged with safe
operation and call identities and does not alter the tool result.

### 5.2 Browser action

1. The conversation actor receives an authorization-required tool result.
2. It validates the action registry and atomically commits
   NyxIdChatActionRequestedEvent.
3. The existing Projection Pipeline fans the committed event to the per-turn
   AGUI session projection and the actor-scoped Studio materialization scope.
   The latter already hosts the shared committed-fact audit materializer and
   writes the action artifact exactly once per actor event.
4. The materializer writes chat.action.requested.
5. A later committed continuation or postcondition fact writes the single
   chat.action.resolved record.

The audit translator is registered on the existing actor-scoped
StudioMaterializationContext, not NyxIdChatSessionProjectionContext; per-turn
session scopes could observe the same actor event more than once. The
materializer consumes committed EventEnvelope facts only. It does not subscribe
to inbound commands, SSE frames, process callbacks, or actor runtime objects.

## 6. Query and authorization

Add GET /api/audit/chat-activity. It is a narrow read facade over
IAuditTrailQueryPort, not a second store.

Supported query parameters:

- cursor;
- from and to;
- take, default 50 and maximum 200;
- surface;
- conversationId;
- outcome.

The endpoint never accepts raw subject. Ordinary callers also cannot select
scope or auditActorId.

### 6.1 Personal read

For an ordinary authenticated caller, the server:

1. resolves exactly one scope and one NyxID subject from the authenticated
   principal;
2. derives the HMAC audit identities for the active key and every retained
   rotation key using the shared canonical-key helper and identity service;
3. creates an AuditTrailQuery with ScopeId and the candidate AuditActorIds fixed
   by the server;
4. requires typed chat provenance to exist;
5. executes filtering in Elasticsearch before pagination.

Missing or ambiguous scope/subject fails closed. No full-scope read followed by
in-memory user filtering is allowed.

Subject resolution is one shared authentication-boundary helper used by both
/api/chat ingestion and Chat Activity reads. It normalizes all recognized
uid/sub/name-identifier/user_id claims, accepts one distinct nonempty value,
and rejects conflicting values. It never silently chooses the first claim.

IAuditActorIdentityHasher therefore gains one narrow operation that returns
the identities for every configured key for a canonical actor key. Audit key
material remains internal to the implementation. Retired keys must stay
configured for at least the 30-day activity retention window; otherwise
personal history under that key becomes intentionally unavailable. The query
store applies an AuditActorIds terms filter, so rotation support does not issue
one query per key or filter records in memory.

### 6.2 Platform-admin read

The same endpoint accepts scope=__all__ only after
IPlatformAdminAuthorizer resolves an elevated Aevatar caller. Admin results may
span all scopes and all HMAC actor identities. A non-admin attempt returns 403;
an unavailable admin authorizer returns 503.

The default for an admin remains personal activity. The UI must require the
explicit All users selection before sending scope=__all__.

### 6.3 Query-store changes

AuditTrailQuery gains typed chat-provenance filters, including an existence
filter used by the Chat Activity endpoint. In-memory and Elasticsearch stores
must implement identical behavior. Elasticsearch mappings explicitly cover
all query-critical chat fields; dynamic mappings are not the query contract.

Schema drift uses the existing fingerprinted-index copy-forward reconcile and
alias switch. No request-time migration or index creation is introduced.

## 7. /admin surface

Add Chat Activity under the existing Overview group, next to Audit Trail. It
reuses the audit list, time formatting, outcome badges, cursor pagination,
loading/error states, and inspector patterns already present in admin.html.

Activity records are organized into Conversation disclosure groups instead of
one flat table. Groups are ordered by their most recent loaded activity. The
most recent Conversation is expanded by default; all other Conversations are
collapsed by default. Each group header makes the Conversation ID prominent and
shows its loaded activity count and latest activity time. Expanding a group
reveals the existing activity rows and row inspector behavior. Records without a
Conversation ID are kept in one explicit `Unattributed Conversation` group.
Loading another page merges newly loaded records into the existing groups.

The expanded-group columns are:

| Column | Value |
|---|---|
| Time | occurred_at |
| Kind | Tool or Action |
| Name | tool name or action kind |
| Status | safe mapped outcome |
| Turn | turn_id, shortened visually but copyable |

The detail inspector may show task ID, step ID, call ID, action request ID,
safe target, side-effect kind, failure code, audit actor ID, scope, and
correlation ID. It must not show omitted fields or fetch a transcript to enrich
the record.

Personal callers see My activity only. Platform admins also see All users and
an optional exact HMAC auditActorId filter. UI visibility is not authorization;
the endpoint independently enforces every read.

The current checkout predates the typed AuditTrail response mapper fix already
present on origin/feature/integrate. Implementation must first preserve or
port that typed mapper behavior rather than building Chat Activity on the old
action/resourceType/resourceId client contract.

## 8. Retention and storage

All records use the existing Elasticsearch audit artifact store. No database
table or second index family is added.

The deployment retention authority deletes audit artifacts whose recorded_at
is older than 30 days only when typed Chat provenance exists. It must not apply
the Chat Activity TTL to unrelated governance artifacts sharing Audit Trail. It
runs outside request and query call stacks with a dedicated least-privilege
Elasticsearch credential. It records counts, duration, cutoff, and failure
status as operational evidence without logging deleted documents.

Previous schema physical indices created by copy-forward reconciliation are
removed only after alias cutover validation and independent count/backup
checks, following the existing audit retention procedure. Release is blocked
unless the active-index retention action and old-physical-index cleanup policy
are both configured.

Expected incremental storage is small:

- tool records already exist; chat provenance adds only bounded scalar fields;
- each action adds no more than two bounded records;
- parameters, results, transcripts, and resources are not duplicated.

Capacity must be measured in staging using real indexed document sizes. The
release evidence records daily tool count, daily action count, primary-store
bytes, replica factor, and projected 30-day headroom. Schema cutover requires
temporary headroom for both old and new physical indices.

## 9. Error handling and consistency

- Audit writes are best effort and idempotent. Chat execution never waits for
  an audit retry loop.
- Query-store failure returns 503 AUDIT_QUERY_UNAVAILABLE and never falls back
  to actor state, event replay, transcript lookup, or raw Elasticsearch access
  from the endpoint.
- Audit activity is eventually consistent. Responses retain the existing
  ingestion watermark and coverage fields.
- Missing owner attribution fails closed from personal results. It is never
  replaced with scope-only attribution.
- Equal audit IDs with different semantic content are conflicts and emit an
  operational error.
- Client pagination uses the existing deterministic occurred_at descending,
  audit_id ascending cursor.

## 10. Verification

### 10.1 Contract tests

- AuditChatProvenance protobuf round-trips all fields.
- Raw owner subject has no field in AuditRecord or its response DTO.
- Tool and action records declare all omitted sensitive fields.
- Stored-record compatibility still reads legacy audit records without
  inventing chat provenance.

### 10.2 Capture tests

- A NyxID Assistant tool call writes one personal Chat Activity record.
- A Workflow Chat tool call writes one workflow-chat record.
- Prompt, arguments, results, access token, owner subject, and secret-shaped
  values are absent from serialized audit text.
- Authorization-required tool execution is not reported as success.
- Action requested is emitted only from NyxIdChatActionRequestedEvent.
- Caller-reported completed emits no terminal action record until a
  postcondition verifies.
- Verified, declined, failed, cancelled, and expired outcomes map exactly once.
- An unverified postcondition leaves the action pending and creates no
  contradictory terminal record.
- Projection redelivery produces a duplicate disposition, not a second record.

### 10.3 Authorization tests

- A user sees their records and not a different HMAC actor in the same scope.
- A user sees records written under every audit identity key retained within
  the 30-day rotation window.
- A user cannot supply scope, auditActorId, or cursor state that widens access.
- A non-admin cannot request __all__.
- An Aevatar admin can query __all__.
- Admin authorization unavailable returns 503 before the query port runs.
- Subject missing or ambiguous returns 401/403 before the query port runs.
- Elasticsearch filtering occurs before limit and cursor pagination.

Use distinct fixtures such as user-audit-alpha, user-audit-beta,
conversation-alpha, turn-alpha, task-alpha, step-alpha, and action-alpha.

### 10.4 UI and operations tests

- /admin exposes Chat Activity to authenticated users.
- All users is absent for ordinary users and present for platform admins.
- The page renders the typed AuditTrail response contract.
- No transcript endpoint is called from Chat Activity.
- Keyboard navigation, focus visibility, empty state, error state, and cursor
  loading remain usable.
- Retention dry run reports the expected count; the governed execution removes
  only records older than the cutoff.
- Audit query readiness and architecture guards remain green.

Required verification includes focused audit/AI/NyxIdChat/BackendConsole tests,
test stability guards, projection boundary guards, architecture guards, docs
lint, the full affected .NET build/test slices, and the frontend-independent
admin asset behavior tests.

## 11. Rollout

1. Land protobuf and query-store schema changes.
2. Reconcile the fingerprinted audit index and verify copy-forward counts.
3. Verify sanitized tool/action records and identity isolation in staging.
4. Configure and dry-run 30-day retention.
5. Deploy capture, query, and the Chat Activity navigation entry together.
6. Monitor audit append failures, query latency, index growth, and retention
   outcomes.

Rollback hides the navigation entry and stops new chat provenance capture.
Existing audit artifacts remain readable through the generic Audit Trail and
expire under the same 30-day policy. No actor state or command decision is
reconstructed from audit data during rollback.

## 12. Acceptance criteria

The feature is accepted when:

- a normal user can see only their own /api/chat tool and action activity;
- an Aevatar platform admin can explicitly select all users;
- no chat content, tool input/output, raw owner identity, or credentials are
  persisted or returned;
- action success requires a committed verified postcondition;
- the feature uses the existing Audit Trail and Projection Pipeline;
- Elasticsearch filtering precedes pagination;
- 30-day retention is configured and exercised;
- affected build, tests, guards, and docs lint pass.
