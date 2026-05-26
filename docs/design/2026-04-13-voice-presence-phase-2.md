# VoicePresence Phase 2

## Scope

This document records the repository-side implementation boundary for phase 2 of issue `#179`.

Phase 2 adds the OpenAI realtime provider implementation behind the phase-1 `IRealtimeVoiceProvider` abstraction. It still does not wire ExternalLink audio ingress or module self-dispatch into a live host session; those remain phase-3 work.

## Delivered

### OpenAI provider package

- Added `Aevatar.Foundation.VoicePresence.OpenAI`.
- Added `OpenAIRealtimeProvider`, implementing `IRealtimeVoiceProvider` on top of the OpenAI .NET realtime client.
- Added a narrow internal session adapter layer so the provider owns:
  - bounded event buffering
  - callback dispatch
  - provider-response-id to voice-response-id mapping
  - mapping from OpenAI GA realtime updates into `VoiceProviderEvent`

### Backpressure

- Provider inbound events are buffered in a bounded `Channel<VoiceProviderEvent>`.
- The channel uses `DropOldest` to prevent unbounded growth if upstream audio/events outrun the callback consumer.
- The drop policy is intentionally applied at the provider boundary, not inside `VoicePresenceModule`, so backpressure remains a transport/provider concern.

### Session configuration

- `VoiceSessionConfig` is translated into OpenAI realtime conversation session options.
- Current implementation validates `PCM16 @ 24000 Hz` only.
- Tool names are registered with a permissive placeholder JSON schema for phase 2 so provider-side tool registration exists before phase-4 tool execution wiring.

## Verified behavior

- OpenAI `response.done(status=cancelled)` is normalized into phase-1 `VoiceResponseCancelled`.
- OpenAI string response IDs are normalized into local monotonic integer response IDs expected by the phase-1 voice state machine.
- Speech-start / speech-stop / audio-delta / function-call / error / disconnect updates are all mapped into `VoiceProviderEvent`.

## Important finding from live verification

- On `2026-04-13`, the realtime API rejected `['text', 'audio']` output modalities for this provider path and accepted audio-only output instead.
- The provider therefore configures `audio` as the output modality in phase 2.

## Non-goals for Phase 2

- No `VoicePresenceModule` self-dispatch wiring yet.
- No ExternalLink / WebSocket / WebRTC transport binding yet.
- No host endpoint or end-to-end user microphone loop yet.
- No tool execution pipeline yet; only provider-side registration and event surfacing.
- No event-injection fence usage yet.

## Verification

- `dotnet build src/Aevatar.Foundation.VoicePresence.OpenAI/Aevatar.Foundation.VoicePresence.OpenAI.csproj --nologo`
- `dotnet test test/Aevatar.Foundation.VoicePresence.OpenAI.Tests/Aevatar.Foundation.VoicePresence.OpenAI.Tests.csproj --nologo`

The OpenAI provider test project includes:

- unit tests for session configuration, tool-result continuation, text injection helper, event mapping, and bounded-channel drop-oldest behavior
- an OpenAI-backed integration test guarded by `OPENAI_API_KEY`
