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
- reserve a run-scoped `ChatTurnHistoryDeliveryGAgent` when a valid `chatHistory` intent and `scope_id` are present
- bind the delivery actor only after the workflow run is accepted
- abandon the reservation when dispatch fails before acceptance

`ChatTurnHistoryDeliveryGAgent` keeps workflow actor, workflow command, delivery, and retry facts only in its operational state. Those IDs are not copied into `ChatTurn`, `ChatTurnAppendedEvent`, or `ChatConversationCurrentStateDocument`.

The delivery actor receives the producer-owned `WorkflowRunTerminalNotification`, validates the delivery, workflow actor, and workflow command identities, and dispatches a single `AppendChatTurnCommand` to `ChatConversationGAgent`. It does not attach a live workflow projection sink for terminal discovery. `COMPLETED`, `FAILED`, and `STOPPED` remain distinct terminal statuses; stopped runs are not archived as failed runs.

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

When starting `/api/chat`, Console sends a typed `chatHistory` write intent:

```json
{
  "chatHistory": {
    "conversationId": "conversation-id",
    "turnId": "stable-turn-id",
    "userText": "original user input"
  }
}
```

`prompt` remains the workflow execution prompt and may include additional context. `chatHistory.userText` is the original user-visible archive text.
