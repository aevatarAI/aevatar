# VoicePresence Phase 6

## Scope

This document records the repository-side implementation boundary for phase 6 of issue `#179`.

Phase 6 adds a MiniCPM-o provider adapter that maps the upstream demo HTTP/SSE protocol into the repository's `IRealtimeVoiceProvider` contract. It does not yet implement WebRTC transport.

## Delivered

### MiniCPM provider adapter

- Added `Aevatar.Foundation.VoicePresence.MiniCPM`.
- Added `MiniCPMRealtimeProvider` as an `IRealtimeVoiceProvider` implementation for the upstream MiniCPM-o demo server.
- The adapter starts a long-lived `/api/v1/completions` SSE loop and translates streamed responses into:
  - `VoiceResponseStarted`
  - `VoiceAudioReceived`
  - `VoiceResponseDone`
  - `VoiceResponseCancelled`
  - `VoiceProviderError`
  - `VoiceProviderDisconnected`

### Audio protocol mapping

- VoicePresence still speaks raw mono PCM16 internally.
- The MiniCPM demo server expects audio chunks wrapped as WAV payloads inside `POST /api/v1/stream`.
- Added local WAV encode/decode helpers so the provider converts:
  - `PCM16 -> WAV` for upstream input
  - `WAV -> PCM16` for downstream SSE audio chunks

### Honest capability boundary

- `MiniCPMRealtimeProvider.SendToolResultAsync(...)` throws `NotSupportedException`.
- `MiniCPMRealtimeProvider.InjectEventAsync(...)` throws `NotSupportedException`.
- `UpdateSessionAsync(...)` only enforces the input sample-rate boundary; upstream demo fields for voice selection, tool registration, and structured event injection do not exist in the source protocol, so those settings are not projected into fake compatibility layers.

### Stop/cancel behavior

- `CancelResponseAsync(...)` calls `POST /api/v1/stop`.
- Because the upstream SSE stream may terminate without an explicit "cancelled" frame, the adapter emits a synthetic `VoiceResponseCancelled` for the currently active response and suppresses any late trailing audio/done frames for that response.

## Upstream mismatch notes

These boundaries were verified against the upstream `OpenBMB/MiniCPM-o` demo server source on `2026-04-14`:

- input path: `POST /stream` and `POST /api/v1/stream`
- output path: `POST /completions` and `POST /api/v1/completions`
- stop path: `POST /stop` and `POST /api/v1/stop`
- request correlation: `uid` header
- output transport: SSE (`text/event-stream`)
- no provider-side tool-calling continuation API
- no structured external event injection API compatible with phase 5

## Tests

- Added `MiniCPMRealtimeProviderTests` covering:
  - WAV input packaging and `uid` header propagation
  - SSE response mapping into `VoiceProviderEvent`
  - synthetic cancel after `POST /stop`
  - HTTP failure propagation
  - unsupported capability boundaries
  - sample-rate validation

## Non-goals for Phase 6

- No MiniCPM-specific host wiring beyond the provider adapter.
- No WebRTC transport or WHIP endpoint yet.

## Verification

- `dotnet test test/Aevatar.Foundation.VoicePresence.MiniCPM.Tests/Aevatar.Foundation.VoicePresence.MiniCPM.Tests.csproj --nologo`
- `bash tools/ci/test_stability_guards.sh`
- `dotnet build aevatar.foundation.slnf --nologo`
- `dotnet test aevatar.foundation.slnf --nologo --no-build`
- `bash tools/ci/solution_split_guards.sh`
