# Implement — Cap text-edit fallback streaming edits

> Prereq: `task.py start` (review gate). Do not implement before activation.
> Coordinate with the parallel session editing channel files; rebase onto
> `origin/feature/integrate` first.

## Ordered checklist

1. **Locate the state proto.** Find the proto/generated type backing
   `AgentRunGAgent` `State.LarkCardDelivery` (search for the message used by
   `GetOrInitLarkCardDeliveryState` at `AgentRunGAgent.LarkCardDelivery.cs:939`).
   Confirm whether `LlmReplyStreamChunkEvent` already has a final/terminal flag.
   - Review gate: opening the `.proto` for edit triggers the interface review
     (CLAUDE.md). Note the change in the PR description.

2. **Add proto fields.**
   - `State.LarkCardDelivery`: `fallback_interim_edits` (int32) — per-turn count
     of dispatched interim text-fallback edits.
   - `LlmReplyStreamChunkEvent`: `is_final` (bool) if not already present.
   - Regenerate; build the proto project.

3. **Thread the final flag.** Update `ToTextStreamChunk` (:1328) to set
   `IsFinal` from the source chunk / finalize signal. Ensure the finalize path
   that runs after `CreationFailed` produces a chunk with `IsFinal = true`.

4. **Enforce the cap in `DispatchTextFallbackChunkAsync` (:816).**
   - read `state = GetOrInitLarkCardDeliveryState()`, `cap = StreamingMaxInterimChunks`.
   - if `chunk.IsFinal`: always dispatch (clear/no-op the counter).
   - else if `state.FallbackInterimEdits >= cap`: return without dispatch (freeze).
   - else: dispatch, then persist `FallbackInterimEdits + 1` via the existing
     state-transition/event path.
   - Apply the `StreamingFlushIntervalMs` (750ms) throttle to interim dispatches
     (track last-dispatch time in state or compare `ChunkAtUnixMs`).
   - Reset `FallbackInterimEdits` to 0 in `LarkCardDeliveryRuntimeState.Initial`
     (already 0 by default) and ensure a new turn starts fresh.

5. **Keep CardKit path untouched** — verify the executor still builds
   `int.MaxValue` + 200ms for `cardMode` (AC4); change only the actor fallback.

## Tests

- Fallback cap: after `StreamingMaxInterimChunks` interim fallback chunks,
  further interim chunks are NOT dispatched to the target sink (assert dispatch
  count == cap).
- Final always lands: send `cap + N` interim then a final → the final IS
  dispatched (assert last dispatch == final complete text).
- CardKit unaffected: a turn that stays in CardKit mode still dispatches all
  interim (no cap) — assert no freeze.
- Throttle: interim fallback dispatches respect 750ms (use a fake `TimeProvider`).

## Validation

```bash
dotnet build aevatar.slnx --nologo                       # or the NyxidChat + proto projects
dotnet test test/<NyxidChat test project>.csproj --nologo
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
```

## Review gates

- Proto/state change → interface review (CLAUDE.md "改动任何 interface … 先开
  issue/PR … 至少 2 人评审"). Call it out explicitly in the PR.
- Confirm AC2 (final never dropped) is covered by a test before merge.

## Rollback

- Pure additive (new proto fields default 0/false; actor-only logic). Revert the
  commit to restore prior behavior; no data migration.

## Commit / PR

- Branch off `origin/feature/integrate` (latest). Conventional message, single
  purpose, e.g. `fix(channels): cap text-edit fallback streaming edits`.
- Isolate from parallel uncommitted work (commit only this task's files).
