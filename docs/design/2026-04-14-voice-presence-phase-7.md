# VoicePresence Phase 7

## Scope

This document records the repository-side implementation boundary for phase 7 of issue `#179`.

Phase 7 adds a WebRTC transport path for `VoicePresence`, including a minimal WHIP-style HTTP offer/answer endpoint and RTP/Opus audio bridging. It completes the implementation plan from issue `#179`.

## Delivered

### WebRTC transport

- Added `WebRtcVoiceTransport` as a second `IVoiceTransport` implementation beside the existing WebSocket transport.
- User audio is exchanged over RTP with Opus payloads.
- User control frames are exchanged over a dedicated WebRTC data channel and remain JSON-encoded `VoiceControlFrame` payloads.
- Provider-facing audio remains raw mono PCM16, so the transport performs local codec conversion only at the boundary.

### Codec bridge

- Added `OpusPcmCodec` using `Concentus` for local `PCM16 <-> Opus` conversion.
- Added fixed runtime options in `WebRtcVoiceTransportOptions`:
  - PCM sample rate
  - Opus frame duration
  - control data-channel label
  - bounded send-buffer capacity
  - ICE gathering timeout

### SIPSorcery adapter

- Added `SipsorceryWebRtcVoicePeer` and `SipsorceryWebRtcVoiceTransportFactory`.
- The adapter negotiates:
  - remote SDP offer intake
  - local SDP answer generation
  - RTP audio send/receive
  - control data-channel send/receive
- ICE gathering waits for either completion or the configured timeout before returning the SDP answer, so the endpoint does not block indefinitely.

### WHIP-style host endpoint

- Added `MapVoicePresenceWhip(...)` to `VoicePresenceEndpoints`.
- `POST` accepts an SDP offer and returns `201 application/sdp` with the generated answer.
- `DELETE` detaches the current transport for the actor.
- The endpoint reuses the existing `VoicePresenceSession` resolution contract and does not add any host-level in-memory actor/session registry.

### Transport lifetime safety

- `VoicePresenceSession` now carries the PCM sample rate required by the resolved voice session.
- `VoicePresenceModule.DetachTransportAsync(...)` now accepts an optional expected transport instance.
- WHIP background cleanup uses that expected instance so a stale completion from an old transport cannot detach a newer one.

## Tests

- Added `WebRtcVoiceTransportTests` covering:
  - PCM buffering before Opus packet send
  - control-frame JSON send
  - incoming Opus/control receive handling
  - disposal behavior
- Added `VoicePresenceWhipEndpointsTests` covering:
  - route registration
  - invalid request rejection
  - successful `POST` attach + SDP answer response
  - `DELETE` detach
  - stale completion not detaching a newer transport

## Non-goals for Phase 7

- No TURN/STUN service provisioning or deployment guidance.
- No multi-party conferencing semantics.
- No browser SDK or front-end WebRTC helper yet.
- No provider-specific SDP customization beyond the narrow transport contract.

## Verification

- `dotnet test test/Aevatar.Foundation.VoicePresence.Tests/Aevatar.Foundation.VoicePresence.Tests.csproj --nologo`
- `bash tools/ci/test_stability_guards.sh`
- `dotnet build aevatar.foundation.slnf --nologo`
- `dotnet test aevatar.foundation.slnf --nologo --no-build`
- `bash tools/ci/solution_split_guards.sh`
