# Design — Cap text-edit fallback streaming edits

## Current behavior (root cause)

Streaming cadence is decided once, at turn start, from the CardKit flag:

`agents/Aevatar.GAgents.NyxidChat/AgentRunReplyGenerationExecutor.cs:537-545`
```csharp
var cardMode = _relayOptions.StreamingCardKitEnabled;            // default true
var throttle = TimeSpan.FromMilliseconds(Math.Max(0, cardMode
    ? _relayOptions.StreamingCardKitFlushIntervalMs              // 200ms
    : _relayOptions.StreamingFlushIntervalMs));                  // 750ms
var maxInterimChunks = cardMode
    ? int.MaxValue                                               // <-- unbounded
    : Math.Max(0, _relayOptions.StreamingMaxInterimChunks);      // 15
return new StreamingReplyRunState(sink, throttle, maxInterimChunks, _timeProvider);
```

`StreamingReplyRunState` (same file, ~779) enforces the cap; final is always
exempt:
```csharp
if (!isFinal && _chunksEmitted >= _maxInterimChunks) { StashPending(text); return; }
```

The card-delivery actor handles the card lifecycle and the fallback:
`agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.LarkCardDelivery.cs`
- `HandleLlmReplyCardStreamChunkAsync` (:70) — each chunk; if the card core
  declines (`return false`) it routes to `DispatchTextFallbackChunkAsync` (:91).
- `HandleLarkCardCreateCompletionAsync` (:363) — on create failure (non
  post-send) → phase `CreationFailed` (:408-418) then `DispatchTextFallbackChunkAsync(ToTextStreamChunk(evt.Chunk))` (:420).
- After `CreationFailed`, `HandleLarkCardStreamingChunkCoreAsync` returns false
  at :294 → every subsequent chunk routes to text fallback.
- `DispatchTextFallbackChunkAsync` (:816) forwards the chunk to
  `State.TargetActorId` (the conversation/text-edit sink) — **no cap, no final
  marker**.
- `ToTextStreamChunk` (:1328) drops any final flag.

Runtime state `LarkCardDeliveryRuntimeState` is a C# record **mapped from the
persisted proto** `State.LarkCardDelivery` (`GetOrInitLarkCardDeliveryState` :939
`var state = State.LarkCardDelivery;`). Phases: Idle / Creating / Streaming /
CreationFailed / Terminated.

### Why the cap is missing on fallback

`cardMode` is true for the whole turn, so the executor builds the state with
`int.MaxValue` + 200ms. The card→text-edit switch happens on the **actor** side
(`CreationFailed`), which the executor never learns about — so the conservative
text-edit cadence (15 / 750ms) is never applied.

## Constraint: the fix must be fallback-specific

Do **not** cap interim in the executor universally. CardKit is intentionally
uncapped (`NyxIdRelayOptions.cs:70-87`): CardKit element-content updates are not
subject to Lark's per-message edit cap, so capping there would re-introduce the
mid-stream freeze the team deliberately removed (regression on the primary path,
violates AC4). The cap must live where the path is known to be text-edit — the
actor, after `CreationFailed`.

## Chosen approach

Enforce the cap on the actor's text-fallback dispatch.

1. **Per-turn interim counter.** Track how many interim text-fallback edits have
   been dispatched for the current turn. After `CreationFailed`, every interim
   chunk goes through `DispatchTextFallbackChunkAsync`; count there.
   - Storage: add a counter to the persisted card-delivery state
     (`State.LarkCardDelivery` proto). **This is a `.proto` change → interface
     review gate (CLAUDE.md: proto/state changes + 2-person review).** The
     counter resets with the per-turn card-delivery state (Initial = 0).
2. **Mark the final chunk.** `ToTextStreamChunk` must carry an `IsFinal`/terminal
   flag so the cap can always let the final through (AC2). Source the flag from
   the card chunk / finalize path. Without this, capping risks dropping the final
   complete text = worse truncation.
3. **Enforce in `DispatchTextFallbackChunkAsync`:**
   - if `chunk.IsFinal` → always dispatch (and clear pending);
   - else if `interimCount >= StreamingMaxInterimChunks` → stash/skip (freeze);
   - else dispatch + increment counter, honoring `StreamingFlushIntervalMs`
     (750ms) throttle for interim (AC3).
   - The target sink already keeps `LastFlushedText`; freezing interim leaves the
     last interim visible until the final lands.

### Alternatives considered (rejected)

- **Executor universal cap** (cardMode → `StreamingMaxInterimChunks`): simplest
  (one line) and final-safe (executor exempts final), but regresses CardKit
  smoothness (AC4 fail). Rejected.
- **In-memory per-turn counter on the actor** (no proto change): avoids the
  review gate, but event-sourced grains discourage in-memory mutable handler
  state; lost on reactivation mid-turn. Acceptable as a fallback if the proto
  change is deemed too heavy, but persisted counter is preferred for correctness.

## Files in scope

- `agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.LarkCardDelivery.cs`
  (counter increment + cap enforcement in `DispatchTextFallbackChunkAsync`;
  `ToTextStreamChunk` final flag; state Initial reset).
- The `.proto` backing `AgentRunGAgent` state (`State.LarkCardDelivery`) — add
  the interim counter field. (Locate via the generated `*.cs` / `protos/`.)
- `LlmReplyStreamChunkEvent` proto — add `IsFinal` if not already present.
- Reuse `NyxIdRelayOptions.StreamingMaxInterimChunks` / `StreamingFlushIntervalMs`
  (no new option needed).
- Tests under `test/…NyxidChat…`.

## Compatibility / rollout

- Backward compatible: new proto fields default to 0/false; CardKit path
  untouched.
- No data migration. Forward-roll only (in-flight turns finish on old code).
- Self-contained; deployable on `feature/integrate`.

## Risk

- Touches an actively-developed actor state machine (parallel session edits
  channel files). Rebase/coordinate before implementing.
- Proto change → review gate; do not merge without the interface review.
- Getting the final-marker wrong = dropped final = worse truncation; cover with a
  test that asserts the final always lands even past the cap.

## VERIFIED 2026-06-24 (first implementation attempt)

The final-marker risk is REAL and load-bearing, confirmed by reading the code:
- The visible final text in the text-edit fallback is delivered via the CHUNK
  stream — the target `ConversationGAgent` edits the Lark message from each
  forwarded chunk; the FinalizeAsync chunk carries the complete text.
- The completion event (`LarkCardDeliveryCompletedEvent`) does NOT re-edit Lark:
  `ConversationGAgent.HandleLarkCardDeliveryCompletedAsync` (:1036) only records
  `ConversationTurnCompletedEvent { Outbound.Text = OutboundText }` +
  `LlmReplyDeliveredEvent` (conversation state/history/projection) — no Lark write.
- => A cap that freezes interim WITHOUT exempting the final chunk truncates
  exactly the long replies we are trying to fix (the final chunk arrives past the
  cap and gets frozen). An in-memory cap alone is therefore UNSAFE.

Correct, minimal shape (chosen for the real implementation):
1. proto: add `bool is_final = 9;` to `LlmReplyCardStreamChunkEvent`
   (`agents/Aevatar.GAgents.Channel.Runtime/protos/conversation_events.proto`).
   ONE field on ONE message — still an interface change → review gate.
2. `TurnStreamingReplySink.DispatchAsync(text, isFinal, ct)` sets `IsFinal`
   (OnDeltaAsync→false, FinalizeAsync→true); `StreamingReplyRunState` (executor)
   passes the `isFinal` it already tracks.
3. `AgentRunGAgent.DispatchTextFallbackChunkAsync(chunk, isFinal)` — always
   forward when `isFinal`; otherwise apply the in-memory interim cap + 750ms
   throttle. Final-exemption makes correctness independent of the counter
   surviving reactivation, so the in-memory counter is now safe.
4. Call sites pass `evt.IsFinal` / `evt.Chunk.IsFinal`.
- The non-card path (executor→target directly) is unchanged (target keeps its own
  cap); only the cardMode→fallback path needs this.
