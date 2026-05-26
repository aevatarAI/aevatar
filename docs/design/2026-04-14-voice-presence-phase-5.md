# VoicePresence Phase 5

## Scope

This document records the repository-side implementation boundary for phase 5 of issue `#179`.

Phase 5 completes the event-injection fence and pending-buffer work for `VoicePresenceModule`. It does not yet implement the MiniCPM-o provider or WebRTC transport.

## Delivered

### Event injection fence

- `VoicePresenceModule` now implements `IRouteBypassModule` so it always observes publication events on the actor pipeline, even when route-filtered modules are configured.
- Non-voice publication events are now candidates for voice-context injection.
- Self-published events are excluded, so the agent does not re-inject its own events back into the provider conversation.
- A local injection fence closes immediately after `IRealtimeVoiceProvider.InjectEventAsync(...)` succeeds and reopens only after the current voice turn becomes safe again.

### Pending buffer

- Added bounded pending buffering in `VoicePresenceModule`.
- When the session is not safe to inject, admitted external events are stored in a FIFO queue with drop-oldest behavior.
- Buffered events are rechecked against `StaleAfter` before flush, so events that waited too long are discarded instead of being injected late.
- Duplicate suppression continues to use `VoicePresenceEventPolicy` with the existing `type + payload` window semantics.

### Provider contract

- Added `VoiceConversationEventInjection` to `voice_presence.proto`.
- Added `IRealtimeVoiceProvider.InjectEventAsync(...)` as the narrow provider-side event-injection port.
- `OpenAIRealtimeProvider` now maps injected events into a structured user text item and starts a follow-up response.

### Payload serialization

- `VoicePresenceModule` now serializes injected protobuf payloads by resolving the concrete message descriptor from the `Any.type_url`.
- If the concrete descriptor cannot be resolved, the module falls back to an opaque JSON object containing `typeUrl` and `valueBase64`, so event injection never fails only because payload JSON formatting is unavailable.

## Tests

- Added `VoicePresenceEventInjectionTests` covering:
  - immediate injection when the fence is open
  - buffering while a response is active
  - stale drop before flush
  - duplicate suppression while buffered
  - drop-oldest buffer behavior
  - self-published event exclusion
- Added `OpenAIRealtimeProviderTests.InjectEvent_should_add_structured_user_message_and_start_response`.

## Non-goals for Phase 5

- No MiniCPM-o provider implementation yet.
- No WebRTC transport or WHIP endpoint yet.

## Verification

- `dotnet test test/Aevatar.Foundation.VoicePresence.Tests/Aevatar.Foundation.VoicePresence.Tests.csproj --nologo`
- `dotnet test test/Aevatar.Foundation.VoicePresence.OpenAI.Tests/Aevatar.Foundation.VoicePresence.OpenAI.Tests.csproj --nologo`
- `bash tools/ci/test_stability_guards.sh`
- `dotnet build aevatar.foundation.slnf --nologo`
- `dotnet test aevatar.foundation.slnf --nologo --no-build`
- `bash tools/ci/solution_split_guards.sh`
