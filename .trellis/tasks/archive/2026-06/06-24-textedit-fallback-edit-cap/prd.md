# PRD — Cap text-edit fallback streaming edits when CardKit create fails

## Problem

The aevatar Lark bot streams replies as **CardKit** cards (primary path). When
`card.create` fails (e.g. the app lacks `cardkit:card:write`, or rate/table
limits), the turn falls back to the **legacy text-edit** path: it sends one Lark
message and edits it in place as the LLM streams.

The fallback currently inherits the **CardKit** streaming cadence that was fixed
at the start of the turn:

- throttle = `StreamingCardKitFlushIntervalMs` (200ms)
- interim cap = `int.MaxValue` (CardKit is deliberately uncapped)

So after a fallback, a long reply produces many fast Lark message edits. Lark
caps edits per message (~20, error code `230072` "The message has reached the
number of times it can be edited"); once the cap is hit even the **final** edit
is rejected, so the user sees a truncated reply. This compounds with
`reply_token_missing_or_expired` on long turns.

The existing `StreamingMaxInterimChunks` (default 15) only applies when the turn
runs in text-edit mode **from the start** — it is never applied to a turn that
*falls back* to text-edit mid-stream.

Observed live (2026-06-24, scope `2c5c9b72…`, Lark app `cli_a9424e6105219eed`):
the fallback path truncates replies; the user explicitly asked to "reduce the
number of edits — after about a dozen edits that still haven't finished, just
wait for the end token and reply once."

## Acceptance criteria

- AC1: When a turn falls back to text-edit (CardKit create failed), interim
  edits are capped at `StreamingMaxInterimChunks` (default 15). After the cap is
  reached, further interim deltas are stashed (no edit dispatched), i.e. the
  message "freezes" on the last interim until the final flush.
- AC2: The **final** flush is always delivered in the fallback path and is never
  dropped by the cap — the user always ends on the complete reply text (subject
  to the reply token still being valid).
- AC3: The fallback also uses the text-edit throttle interval
  (`StreamingFlushIntervalMs`, 750ms), not the 200ms CardKit interval.
- AC4: The CardKit primary path is unchanged — still uncapped interim
  (`int.MaxValue`) at 200ms; no new freeze/choppiness for card replies.
- AC5: Behavior-change is covered by tests (cap enforced on fallback; final
  always delivered; CardKit path unaffected).
- AC6: `dotnet build` + relevant tests pass; `bash tools/ci/test_stability_guards.sh`
  and `bash tools/ci/architecture_guards.sh` green. Any `.proto` change is
  flagged for the interface review gate (CLAUDE.md).

## Non-goals

- Not fixing `cardkit:card:write` provisioning (Lark-console / NyxID side) — that
  is what retires the fallback path in the first place. Tracked separately
  (issues #2355 / #2357).
- Not changing the reply-token TTL / acquisition (`reply_token_missing_or_expired`
  is a related but distinct failure; capping edits reduces but may not fully
  eliminate it).
- Not changing CardKit streaming behavior.

## Context / references

- Memory: `project_lark_ornn_skill_truncated_reply_card_scope_token_expiry` (续2/续4).
- Sibling fix already shipped this session: slash multi-line argument truncation
  (`SkillInvocationTriggerParser`, commit `1db636d1b`) — independent bug.
- Related GitHub issues: #2355, #2357.
