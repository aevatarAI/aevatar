# NyxIdChat Multi-Turn And Authorization Terminal Design

## Problem

NyxIdChat currently lets the caller-supplied `sessionId` become
`ChatRequestEvent.SessionId`. `RoleGAgent` correctly owns that value as its
per-request replay key and rejects a second prompt under the same key. Local and
Orleans dispatch only acknowledge inbox admission, so a later actor-handler
exception is not returned to the HTTP call. Without a committed terminal fact,
the session projection remains open and SSE emits only keepalives.

NyxID proxy failures have a structured external contract (`error`,
`error_code`, HTTP status), but the Aevatar tool boundary currently returns the
failure as an ordinary JSON string. The tool loop can therefore treat an
authorization failure as a successful result or omit a receipt entirely.
Role completion and AGUI projection have no typed authorization blocker to map.

Approval has the same identity defect in a different form: the endpoint observes
the caller's session value while the actor creates a new random session for an
approved continuation. The observation scope and continuation can therefore be
different.

## Identity Contract

- `actorId` is the stable conversation identity and remains the actor address.
- `turnId` is the server-owned identity of one user submission or approval
  continuation. It is the value carried by `ChatRequestEvent.SessionId` because
  that existing RoleGAgent field is the per-turn replay key.
- `clientRequestId` is optional transport idempotency input. The server derives a
  stable turn id from `actorId + clientRequestId`; callers never supply turn ids.
- `commandId` and `correlationId` remain independently generated dispatch and
  trace identities.
- approval `requestId` remains the only authority for locating pending approval
  state. A server-created continuation turn id controls only observation and the
  resumed chat turn.
- legacy request `sessionId` remains accepted only as a deprecated, ignored
  field. It never becomes an internal turn id.

Without `clientRequestId`, every submission receives a fresh random turn id.
With `clientRequestId`, a retry receives the same derived turn id. RoleGAgent's
existing prompt and multimodal equality checks provide actor-owned replay and
conflict enforcement. No process-local id map is introduced.

## Command And Projection Flow

1. The Host validates authentication, scope admission, and input.
2. The Host resolves an optional body `clientRequestId` or `Idempotency-Key`
   header and creates the turn id.
3. `RUN_STARTED` exposes `actorId` and `turnId` before dispatch.
4. `NyxIdChatCommand` carries `TurnId`; its envelope maps that value to
   `ChatRequestEvent.SessionId` while command and correlation ids remain
   separate.
5. Observation is attached to `actorId + turnId` through the existing actorized
   Projection Pipeline.
6. RoleGAgent commits a completion, authorization blocker, or idempotency
   conflict fact. The projector maps committed facts to AGUI terminal frames.
7. SSE writes terminal identity and stops heartbeat before closing.

An identical idempotent retry replays the already committed RoleGAgent session.
A `terminal_time` protobuf field is assigned once when RoleGAgent commits the
authoritative completion, persisted in `RoleChatSessionState`, and copied into
the replayed completion event. NyxIdChat history archival uses that stored time
instead of sampling a new clock value. The history actor therefore receives an
identical payload and keeps its strict conflict detection while deduplicating
the replay without appending a turn or persisting a rejection.

A different prompt or multimodal input under the same derived turn id commits a
typed conflict event without overwriting the original replay record. The AGUI
adapter emits `RUN_ERROR` with code `IDEMPOTENCY_CONFLICT` and the turn id.

## Actor Failure Boundary

RoleGAgent wraps the full chat handler, not only the LLM stream enumeration.
Known replay conflicts commit a dedicated conflict event. Other handler failures
for a valid turn commit a failed `RoleChatSessionCompletedEvent` with a stable
error code and safe message. Internal exception messages, tokens, and credentials
are logged server-side but are not copied to client frames.

Dispatch and projection-attach failures remain endpoint-visible and produce a
typed `RUN_ERROR`. Streaming/presentation failures stop heartbeat and attempt a
generic safe `RUN_ERROR`. Cancellation stops heartbeat without starting new
work. A committed terminal is the normal cross-runtime guarantee; an event-store
failure cannot truthfully claim a committed terminal and is handled by the
endpoint's bounded interaction failure path.

## Authorization Contract

`ai_messages.proto` defines `NyxIdAuthorizationRequiredEvent` with:

- `service_slug`
- optional `service_label`
- optional `resource_uri`
- `reason_code`
- `safe_message`

`AgentToolReceipt` can carry that typed blocker. A result-receipt hook on
`IAgentTool` lets NyxID tools map their structured response before the generic
receipt factory labels it successful. Only the published NyxID
`401/unauthorized/1001` tuple means that the caller credential is invalid or
expired. The shared `403/forbidden/1002` tuple is a normal typed tool failure:
NyxID also uses it for approval-policy denial and approval timeout, so it cannot
prove that reconnecting would help. Classification uses typed status, key, and
numeric code, never exception-message substring matching.

Missing service connections use positive Aevatar-owned evidence. Dynamic tool
discovery exposes only connected services, and `nyxid_require_service` emits a
typed `NYXID_SERVICE_NOT_CONNECTED` blocker when a required service is absent.
All other proxy failures receive credential-free error receipts and safe tool
results. Raw proxy error bodies, messages, credentials, and failed-call resource
query strings do not enter receipts, committed completion state, replayed tool
frames, or SSE.

`RoleChatSessionCompletedEvent` carries a typed outcome and optional
authorization blocker. When any tool receipt requires authorization, the actor
commits outcome `BLOCKED`. Projection emits:

1. `CUSTOM` named `nyxid.authorization.required`, with a credential-free payload.
2. `RUN_FINISHED` with typed AGUI status `BLOCKED`, the same turn id, and the
   typed blocker packed as the result.

The blocker terminates only the current turn. It does not create pending tool
approval state, clear history, deactivate the actor, or automatically resume the
turn. After connecting the service, the caller sends a new turn. `:approve`
continues to apply only to `PendingToolApprovalState` selected by `requestId`.

For a missing connected service where no service operation tool exists,
NyxIdChat exposes a dedicated `nyxid_require_service` tool. Its deterministic
receipt carries the same typed blocker. The system prompt instructs the model to
use this tool instead of explaining a missing service only in natural language.

## Approval Continuation

The approval endpoint ignores legacy session identity and creates a continuation
turn id. `ToolApprovalDecisionEvent` carries that id separately from the pending
`requestId`. On approval, RoleGAgent uses the supplied continuation turn id for
the self-message and restores the original typed scope from pending state. On
denial or continuation failure, the same continuation turn id receives the
terminal fact. A stale or unknown request id remains a no-op at the actor
boundary and cannot approve another request.

## Chat History

The conversation actor instance retains `ChatHistory` across turns because the
same `actorId` is reused. Each LLM request therefore includes prior user,
assistant, and valid tool transcript messages. The external Studio history append
uses message ids derived from the turn id. Completed, failed, and blocked turns
are distinguished without closing the conversation or preventing a later turn.

## Tests

The regression suite covers:

- two prompts on one actor use distinct server turn ids and the second LLM
  request contains first-turn history;
- repeated legacy `sessionId` values do not control internal turn identity;
- identical `clientRequestId` and input replay without a second LLM execution;
- changed input under one `clientRequestId` yields typed conflict;
- actor/dispatch/projection/stream failures produce terminal frames and stop
  heartbeat;
- a real structured NyxID authorization error becomes the custom event and a
  blocked terminal without credentials;
- `nyxid_require_service` produces the same typed path without calling a missing
  service operation;
- a blocked turn does not prevent the next turn on the same actor;
- approval continuation remains bound to the original pending `requestId` while
  using a server-owned continuation turn id.
