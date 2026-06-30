# Fix voice barge-in cancel-race surfaced as fatal error

## Goal

The `/voice` page (real in-browser `/ws/voice` client) shows a fatal red "出错 ·
provider 错误：Cancellation failed: no active response found" during a normal
session. Stop a benign, expected realtime race from being surfaced to the client
as a fatal error so the voice session stays usable.

## Root cause (confirmed by live trace)

Deployed commit `af1c15f6` (origin/feature/integrate). Single `/ws/voice`
session in a 45-min window, scope `5d0d7b72`:

```
Voice upstream operation response.cancel delivered via live relay lease=cfd3d10b…
warn  OpenAI realtime error code=response_cancel_not_active
      message=Cancellation failed: no active response found
```

- Session runs server VAD with `interrupt_response: true`
  (`OpenAIRealtimeProviderOptions.InterruptResponseOnSpeech = true`). On barge-in
  OpenAI cancels the in-progress response server-side.
- `VoicePresenceModule` SpeechStarted branch ALSO sends an explicit
  `response.cancel`. It arrives after OpenAI already cancelled → OpenAI returns
  `response_cancel_not_active`.
- The provider maps that to a `VoiceProviderError`, the module publishes it as a
  `VoiceRealtimeFrame{Error}`, and the browser client renders ANY error frame as
  a terminal "出错" state. The agent itself worked (connected, got tools,
  produced a response); only the benign race is shown as fatal.

A cancel whose goal is "no active response" that finds "no active response" has
succeeded; it is idempotent, not a failure.

## Requirements

- Classify benign/idempotent realtime race errors at the OpenAI provider boundary
  and do NOT surface them as client-facing `VoiceProviderError` events:
  - `response_cancel_not_active` (cancel when no active response).
  - `conversation_already_has_active_response` (symmetric race: response.create
    while a response is already active) — same class, already flagged in code.
- Keep observability: still log these (at Debug — they are routine), but do not
  forward them as error frames.
- Genuine provider errors (e.g. `rate_limit`, auth) must continue to surface as
  `Error` events unchanged.
- Do not change the explicit barge-in cancel itself (it is provider-agnostic and
  idempotent; needed for providers without server-side interrupt).

## Acceptance Criteria

- [x] A `response_cancel_not_active` realtime error does NOT produce a
      `VoiceProviderEvent.Error` (regression test on the provider receive loop).
- [x] A `conversation_already_has_active_response` realtime error does NOT produce
      a `VoiceProviderEvent.Error`.
- [x] A genuine error (`rate_limit`) still produces a `VoiceProviderEvent.Error`
      (no regression to existing mapping test).
- [x] `dotnet test` for `Aevatar.Foundation.VoicePresence.OpenAI.Tests` passes
      (31 passed, 1 skipped); architecture + test-stability guards pass.
- [x] Change committed and pushed to `feature/integrate` (`159586d23`).

## Outcome

Fixed in `OpenAIRealtimeProvider` (`MapSessionEvent` drops benign race codes;
receive loop logs them at Debug). TDD: test RED (errors=3) before, GREEN
(errors=1) after. Pushed to `feature/integrate` as `159586d23`; auto-deploys.
No spec/ADR change — behavior correction within the existing OpenAI adapter
boundary, documented by code comment + regression test.

## Notes

- Fix scoped to the OpenAI adapter boundary
  (`src/Aevatar.Foundation.VoicePresence.OpenAI/OpenAIRealtimeProvider.cs`),
  where OpenAI-specific error semantics belong.
- Optional follow-up (not in scope): make the browser client not treat every
  error frame as terminal (defense-in-depth).
- Implemented in a clean worktree at origin tip; local checkout is behind origin.
