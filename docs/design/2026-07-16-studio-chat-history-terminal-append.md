# Studio ChatHistory Terminal Append

## Decision

Studio ChatHistory V1 uses `ChatConversationGAgent` as the sole authoritative owner of a conversation archive. Chat history writes are append-only terminal `ChatTurn` facts addressed by stable `turn_id`; they no longer replace the full transcript and no longer fan out through `ChatHistoryIndexGAgent`.

This follows the repository CQRS/event-sourced actor framing:

- write path: command to actor-owned state, then committed event publication
- read path: typed read model query, never actor state unpacking or event replay
- list path: query `ChatConversationCurrentStateDocument` by `scope_id`, not a second index actor

## Transcript Contract

`AppendChatTurnCommand` carries only V1 archive facts:

- `scope_id`
- `conversation_id`
- `turn.turn_id`
- `turn.user_text`
- `turn.assistant_text`
- `turn.terminal_status`
- `turn.sanitized_error`
- `turn.terminal_time`
- optional `turn.llm_route`
- optional `turn.llm_model`

It does not carry workflow `run_id`, workflow `command_id`, thinking, reasoning, tool calls, steps, runtime events, approval/intervention state, or browser lifecycle details.

Every committed terminal turn remains part of the user-queryable transcript until the user explicitly deletes the entire conversation. The current contract has no per-turn TTL, rolling eviction, segment/archive tier, or background transcript cleanup. `ChatConversationGAgent` owns both the committed turns and the deletion fact; `ConversationDeletedEvent` makes the whole conversation unavailable through the official query surface but does not silently prune individual turns.

The transcript is not an LLM prompt window. Continuation admission reads the already-materialized transcript and deterministically selects at most the latest 24 nonblank user/assistant messages for execution context. That selection is ephemeral application input: it does not mutate actor state, remove projected turns, or change what `GET /api/chat/conversations/{conversationId}` returns.

## Actor Invariants

`ChatConversationGAgent` enforces:

- actor-owned, persisted `next_turn_sequence` beginning at `1`
- strictly monotonic turn `sequence` that is never derived from the current turn count
- duplicate `turn_id` with identical payload is idempotent
- duplicate `turn_id` with different payload records `Conflict`
- append remains available after 250 turns
- committed turns are never trimmed as a prompt-window implementation detail

Snapshots created before `next_turn_sequence` existed recover the initial waterline from already committed turn sequence identities once. Every subsequent append advances the persisted waterline atomically with `ChatTurnAppendedEvent`, so passivation, command replay, and projection replay cannot reuse a turn sequence.

## Workflow Terminal Delivery

`WorkflowChatRunInteractionService` owns the Studio/Workflow handoff boundary for `/api/chat`:

- preserve trusted caller-provided `CommandIdSeed` and `CorrelationIdSeed`
- resolve the workflow actor target
- reserve a run-scoped `ChatTurnHistoryDeliveryGAgent` when a typed `conversation` intent and trusted `scope_id` are present
- bind the delivery actor only after the workflow run is accepted
- abandon the reservation when dispatch fails before acceptance

`ChatTurnHistoryDeliveryGAgent` keeps workflow actor, workflow command, delivery, and retry facts only in its operational state. Those IDs are not copied into `ChatTurn`, `ChatTurnAppendedEvent`, or `ChatConversationCurrentStateDocument`.

The delivery actor receives the producer-owned `WorkflowRunTerminalNotification`, validates the delivery, workflow actor, and workflow command identities, and dispatches a single `AppendChatTurnCommand` to `ChatConversationGAgent`. It does not attach a live workflow projection sink for terminal discovery. `COMPLETED`, `FAILED`, and `STOPPED` remain distinct terminal statuses; stopped runs are not archived as failed runs.

Create versus continue is explicit in the delivery reservation. A create reservation may create the `ChatConversationGAgent` if it is absent. A continue reservation must not silently create a missing conversation; missing, deleted, or wrong-scope conversations fail before append.

## Read Models

`ChatConversationCurrentStateDocument` is typed. It exposes the conversation summary fields and the detail `turns` list directly:

- `scope_id`
- `conversation_id`
- `title`
- `service_id`
- `service_kind`
- `created_at_ms`
- `updated_at_ms`
- `message_count`
- `llm_route`
- `llm_model`
- `deleted`
- `turns`

Query paths consume this document directly. The detail query returns the complete committed transcript while the conversation is active; prompt-window selection is a separate continuation-admission concern. Queries do not unpack `state_root`, read actor internals, replay events, or prime projection during query execution.

## Actor Identity And Ownership

`ChatConversationGAgent` actor IDs are opaque server identities. New writes derive the conversation actor from an injective encoding of the `(scope_id, conversation_id)` tuple, so tuples such as `(tenant, admin-c1)` and `(tenant-admin, c1)` cannot resolve to the same actor.

Read and delete paths do not trust the route-derived actor ID alone. Conversation detail, continuation admission, and delete first load the projected `ChatConversationCurrentStateDocument`, then admit the request only when the stored `scope_id` and `conversation_id` exactly match the requested tuple and `deleted` is false.

Rollout behavior is narrow:

- new conversation writes use only the opaque actor ID
- reads, continuation admission, and deletes try the opaque actor ID first
- legacy `chat-{scopeId}-{conversationId}` lookup is a fallback only for existing materialized conversations
- a legacy fallback document is usable only when its stored `scope_id` and `conversation_id` match the request

Callers must never parse actor IDs or infer ownership from an actor ID string.

## Index Pagination

`GET /api/scopes/{scopeId}/chat-history` returns a typed page result:

```http
GET /api/scopes/scope-1/chat-history?pageSize=50&cursor=opaque-cursor
```

The response contains `conversations` and `nextCursor`. `pageSize` defaults to `50` and is capped at `200`; it controls conversation-index pagination only and does not limit turns within a transcript. The index query keeps the existing `scope_id` filter, excludes `deleted` conversations, orders by `updated_at_ms` descending, and uses `conversation_id` ascending as the stable tie-breaker. Clients pass `nextCursor` back as `cursor` to load the next page.

The cursor is opaque. Clients must not parse it or derive conversation identity from it.

## Create Idempotency And Recovery

Persistent conversation create is retryable by a client-controlled typed command identity. The HTTP body uses `"commandId"`; this maps to `WorkflowChatRunRequest.CommandIdSeed` and is not carried through `Metadata`, headers, or any other business-semantic bag.

To create a new persistent conversation, the client sends `conversation: {}` and a stable `"commandId"`:

```json
{
  "prompt": "summarize the release plan",
  "commandId": "client-create-command-1",
  "conversation": {}
}
```

The authenticated scope and `"commandId"` derive the create recovery identity, the new `conversationId`, the new `turnId`, and the delivery actor identity. Repeating the same create request with the same scope and `"commandId"` must not start a second workflow/chat side effect. Reusing the same scope and `"commandId"` with a materially different request returns `IDEMPOTENCY_CONFLICT` with HTTP `409`.

The recovery read model is `ChatHistoryCreateRecoveryCurrentStateDocument`, materialized from committed `ChatTurnHistoryDeliveryState` for create reservations. It is keyed by the authenticated scope and workflow command identity, stores the conversation and turn identities when allocated, and exposes the recovery status plus source version. Recovery queries read only this already-materialized read model; they do not read actor internals, replay events, or prime projection.

The narrow recovery endpoint is:

```http
GET /api/scopes/scope-1/chat-history/create-recovery/client-create-command-1
```

It returns `404` when no matching scope-bound recovery document exists. Otherwise it returns the stored `conversationId`, `turnId`, workflow command/correlation details, `requestFingerprint`, `stateVersion`, `updatedAt`, and one of these status values:

- `reserved`
- `bound`
- `append_dispatched`
- `abandoned`
- `failed`
- `append_committed`
- `append_rejected`

## Console Boundary

Console no longer sends remote full-transcript saves to `/api/scopes/{scopeId}/chat-history/conversations/{conversationId}`. That public `PUT` surface was removed. Durable Chat History is owned by the backend `ChatConversationGAgent` and its current-state read models.

`POST /api/chat` is the generic Workflow Chat HTTP/SSE capability. Its public request body treats legacy `scopeId` as ignored compatibility input; the trusted scope comes from the authenticated principal. It does not accept legacy `chatHistory`, and `chatHistory.conversationId` never selects a Conversation. Chat History persistence is an explicit opt-in through `conversation`:

```json
{
  "prompt": "summarize the release plan"
}
```

This is stateless Workflow Chat and does not create a `ChatConversationGAgent`, delivery reservation, or Chat History read model.

To create a new persistent Conversation, the client asks for a new conversation without supplying `conversationId` and supplies a stable `"commandId"` for retry and recovery:

```json
{
  "prompt": "summarize the release plan",
  "commandId": "client-create-command-1",
  "conversation": {}
}
```

To continue an existing Conversation under the authenticated scope, the client supplies only the existing `conversationId`:

```json
{
  "prompt": "continue with risks",
  "conversation": {
    "conversationId": "conversation-id"
  }
}
```

Chat History integration owns the canonical `conversationId` for create and owns every persistent `turnId`. A blank `conversation.conversationId` is invalid. A nonblank `conversation.conversationId` that is absent, deleted, or outside the trusted scope returns `CONVERSATION_NOT_FOUND`; it must not fall back to create.

When a persisted `/api/chat` request is accepted, the SSE stream first emits `aevatar.chat.context` with `WorkflowChatContextPayload(scope_id, conversation_id, turn_id)`, then emits the existing `aevatar.run.context` frame. `aevatar.chat.context` means the identities were allocated and the terminal delivery reservation was established; it does not mean the Conversation read model is already visible or that the terminal turn has committed.

`prompt` remains the workflow execution prompt and is the archived user text for backend terminal append. `sessionId` remains runtime correlation only and is never used as Conversation identity.
