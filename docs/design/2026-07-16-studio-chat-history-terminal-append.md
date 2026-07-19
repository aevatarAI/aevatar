# Studio ChatHistory Terminal Append

## Decision

Studio ChatHistory V1 uses `ChatConversationGAgent` as the sole authoritative owner of a conversation archive. Chat history writes are append-only terminal `ChatTurn` facts addressed by stable `turn_id`; they no longer replace the full transcript and no longer fan out through `ChatHistoryIndexGAgent`.

This follows the repository CQRS/event-sourced actor framing:

- write path: command to actor-owned state, then committed event publication
- read path: typed read model query, never actor state unpacking or event replay
- list path: query `ChatConversationCurrentStateDocument` by `scope_id`, not a second index actor

## Archive Contract

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

## Actor Invariants

`ChatConversationGAgent` enforces:

- monotonic `sequence` beginning at `1`
- `MaxTurns = 250`
- duplicate `turn_id` with identical payload is idempotent
- duplicate `turn_id` with different payload records `Conflict`
- the 251st non-duplicate turn records `MaxTurnsExceeded`
- existing turns are not trimmed when quota is exceeded

Quota rejection is an archive boundary: an already accepted/completed workflow run whose terminal append is rejected by `MaxTurns` is not represented as an archived ChatHistory turn.

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

Query paths consume this document directly. They do not unpack `state_root`, read actor internals, replay events, or prime projection during query execution.

## Console Boundary

Console no longer sends remote full-transcript saves to `/api/scopes/{scopeId}/chat-history/conversations/{conversationId}`. That public `PUT` surface was removed. Console still maintains local browser fallback state for responsive UI recovery.

`POST /api/chat` is the generic Workflow Chat HTTP/SSE capability. Its public request body does not accept `scopeId` or `chatHistory`; the trusted scope comes from the authenticated principal. Chat History persistence is an explicit opt-in through `conversation`:

```json
{
  "prompt": "summarize the release plan"
}
```

This is stateless Workflow Chat and does not create a `ChatConversationGAgent`, delivery reservation, or Chat History read model.

To create a new persistent Conversation, the client asks for a new conversation without supplying either durable identity:

```json
{
  "prompt": "summarize the release plan",
  "conversation": {
    "conversationId": null
  }
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
