# NyxIdChat Committed Progress And Tool Identity Design

Issue: #2893

## Problem

`RoleGAgent` consumes `ChatStreamAsync` incrementally, but the NyxIdChat
projection ignores transient text, reasoning, media, tool, and usage messages.
It only consumes the committed `RoleChatSessionCompletedEvent`, then
`NyxIdChatCompletionAguiFrameBuilder` expands the final snapshot into all AGUI
frames. The SSE writer already writes and flushes every frame, so the latency is
caused by the committed projection input rather than HTTP buffering.

Tool cards also conflate the LLM invocation protocol name with display identity.
`ToolCallEvent`, AGUI `ToolCallStartEvent`, SSE, and the web accumulator retain
only `toolName`. `NyxIdConnectedServiceToolSource` discovers the legacy proxy
service list and reduces each connection to slug/id, discarding connection,
catalog, and presentation identity.

## Change A: Typed Tool-Card Identity

Tool invocation and presentation remain separate contracts. `toolName` stays
the executable protocol identifier. A provider-owned
`ToolPresentationDescriptor` is snapshotted when an invocation starts and is
transported with that historical invocation.

The descriptor is a Foundation protobuf contract so both AI and AGUI depend on
the same lower-layer type. It contains:

- `invocation_name`, `display_name`, `description`, `kind`, `availability`,
  `unavailable_reason`, and optional `icon_url`;
- a typed `source_ref` oneof containing `BuiltInToolRef`,
  `NyxIdOperationRef`, `McpToolRef`, or `SkillRef`;
- NyxID operation identity containing `connected_service_id`, `service_slug`,
  `catalog_service_slug`, `connection_label`, `connector_display_name`, and the
  operation id/method/path.

`IAgentTool` exposes a static descriptor with a generic default plus an
invocation-aware resolver for argument-dependent identity. Repository-owned
built-in, MCP, skill, and NyxID tools provide explicit descriptors. The runtime
normalizes a provider descriptor so `invocation_name` always equals the actual
registered tool name. `use_skill` resolves `SkillRef.skill_name` from its typed
JSON argument at invocation start; it never substitutes the protocol id as a
skill identity. Unknown tools use an available generic fallback rather than
guessing identity from name prefixes.

`ToolCallEvent` and AGUI `ToolCallStartEvent` carry the descriptor cloned at
invocation start. Completion copies that clone instead of consulting the live
tool provider again. SSE
serializes it as structured JSON, and the frontend stores it on the tool-call
record. Cards use the snapshotted display name and description while retaining
the invocation name for diagnostics. Later provider or catalog renames cannot
rewrite historical cards.

### NyxID Discovery Authority

Caller-scoped `GET /api/v1/keys` is the connected-instance authority, and an
active key is itself connection evidence. Explicit `connected=false` rejects
the entry; absent or true `connected` still requires `is_active=true`,
`status=active|ready`, and no explicit caller or credential-source denial
before the instance enters effective executable tools.
`GET /api/v1/catalog` supplies definition identity, connector name,
description, and icon. A typed adapter DTO retains both sources and joins them
by the key's explicit catalog slug; no `toolName` prefix parsing or metadata bag
is allowed.

The proxy-aware OpenAPI endpoint remains the operation-definition source used
to materialize executable operations. Its service id is the connected key id,
not a catalog id. User and organization credentials are discovered separately
and retain their token ownership. No process-local catalog is introduced.

There is no current consumer that needs a pre-run effective-tools query, so this
change does not add one. Future callers must reuse the same authenticated,
caller-scoped discovery path rather than create a second directory.

## Change B: Committed Session Progress

`RoleChatSessionProgressedEvent` is the single committed progress contract. It
contains the session/turn id, an actor-owned monotonic progress sequence, and a
typed oneof for text start/delta/end, reasoning, media, tool start/result,
tool approval, usage, authorization notice, terminal status, and explicit replay snapshot.

`RoleChatSessionState.last_progress_sequence` is distinct from the existing
session ordering `sequence`. The Role actor increments it through typed progress
creation. Each ordinary progress fact is persisted through
`PersistDomainEventAsync`. Completion embeds its remaining typed terminal tail
inside one `RoleChatSessionCompletedEvent`, so event-store commit and committed
publication cannot split final authority from terminal presentation. State
replay restores the sequence watermark from that embedded tail. The existing
committed `EventEnvelope<CommittedStateEventPublished>` remains the sole
Projection Pipeline input.

The live path maps one committed progress fact to one AGUI frame. AGUI and SSE
carry the committed sequence. Completion remains the final state authority and
completion-notification source, but a normal live completion does not expand
snapshot text, tools, or usage. The projector expands only the completion's
embedded typed tail: any missing final text, usage, text end, authorization
notice, and exactly one run-finished or run-error. The completion snapshot is
not consulted. One committed envelope therefore makes final authority and its
terminal presentation observable together.

Projection scope state keeps a successful source-version watermark per origin
actor. That preserves multi-publisher projection scopes while rejecting a
duplicate or stale envelope from the same authoritative actor before fan-out.
Each explicit sink attachment also fences post-fan-out retry delivery by latest
actor sequence and exact protobuf bytes at that sequence. It preserves distinct
multi-frame replay output sharing one sequence, owns no business fact, and is
released with the attachment instead of entering a process-local registry.

Explicit replay is different: the actor commits a typed replay progress payload
containing the already committed completion snapshot. Only that payload invokes
the batch completion frame builder. It restores tools, reasoning, media, text,
usage, and terminal presentation from that snapshot in stable order. Replay
frames retain the replay progress sequence and are not mixed with the original
live sequence.

### Tool Lifecycle Ordering

`ChatRuntime` emits runtime-only typed tool lifecycle chunks. When a streamed
tool call becomes complete, the iterator first yields `tool started`. The Role
actor persists its progress before requesting the next iterator item. Only when
iteration resumes may `StreamingToolExecutor.AddTool` start execution. This
guarantees committed `TOOL_CALL_START` is observable before tool completion,
including synchronously completing tools.

Every tool result, including ordinary read-only successes that do not produce a
special receipt, is yielded as a typed completed chunk. This supplies the live
tool-result frame and the committed snapshot without inferring results from
history after the stream ends. Text-parsed tool calls and initial skill recovery
use the same lifecycle.

The flow remains actor-turn-owned. It introduces no `Task.Run`, callback state
mutation, stream request-reply, session registry, direct HTTP write, or
`ChatAsync` path.

## Ordering And Idempotency

For a normal turn the observable order is:

1. committed text start;
2. committed content/reasoning/media/tool progress in actor sequence order;
3. one committed completion authority containing the typed terminal tail;
4. projection mapping of that tail and per-frame SSE write/flush.

Projection entries preserve the actor progress sequence. Repeated or older
progress cannot advance the actor state and downstream tests assert strictly
increasing live sequences. A live completion never expands its snapshot, so it
cannot duplicate already delivered text or tools; only its typed tail produces
presentation frames. A different-input retry commits a typed command-attempt
rejection with its own command id and leaves the completed session sequence and
final authority unchanged. New producers use the command-attempt contract;
projection activation and mapping retain the legacy session-conflict protobuf
full name for queued events and rolling upgrades. The normal observation
watermark drops duplicate/stale delivery, while explicit replay of a recorded
projection failure bypasses that fence so an older failed frame remains
recoverable after a newer version succeeds. The attachment-scoped delivery fence closes the
remaining post-fan-out retry window without collapsing distinct frames emitted
by one explicit replay progress fact.

## Verification

Change A is independently verified by protobuf/descriptor tests, NyxID
`/keys` + `/catalog` discovery tests, built-in/MCP/skill descriptor tests, AGUI
and SSE transport tests, and frontend normalization/card snapshot tests.

Change B is independently verified with a controlled streaming provider and
controlled tool using `TaskCompletionSource`/`Channel`. The test observes the
first committed text frame before releasing provider completion, observes tool
start before releasing tool completion, and asserts ordered sequences,
per-frame delivery, replay-only snapshot synthesis, and exactly one terminal
frame without `Task.Delay` or polling.
