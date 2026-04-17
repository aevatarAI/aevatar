# VoicePresence Phase 1

## Scope

This document records the repository-side implementation boundary for phase 1 of issue `#179`.

Phase 1 only establishes the foundation-layer contracts and the local module/state-machine skeleton for voice presence. It does not introduce a second orchestration path, and it does not add network/provider runtime integration into Host or Application layers yet.

## Delivered

### Foundation abstractions

- Added `Aevatar.Foundation.VoicePresence.Abstractions`.
- Added `voice_presence.proto` as the strong-typed protocol for provider/session/audio/control frames.
- Added `IRealtimeVoiceProvider` as the runtime-neutral provider port.
- Added `IAudioFastPath` as the narrow audio ingress abstraction.
- Added `ILifecycleAwareEventModule` so module startup and teardown can bind to agent lifecycle without leaking provider setup into ad hoc call sites.

### Foundation implementation

- Added `Aevatar.Foundation.VoicePresence`.
- Added `VoicePresenceStateMachine` to model `Idle -> UserSpeaking -> ResponseInProgress -> AudioDraining` and drain-ack fencing.
- Added `VoicePresenceEventPolicy` for stale-event rejection and short-window dedupe.
- Added `VoicePresenceModule` as the phase-1 EventModule skeleton:
  - owns provider/session initialization
  - handles typed provider/control events
  - exposes raw audio fast-path forwarding
  - performs response cancel on barge-in when speech starts during an active response

### GAgent runtime support

- `GAgentBase` now caches the materialized event pipeline and invalidates that cache when modules change.
- `GAgentBase` now initializes and disposes `ILifecycleAwareEventModule` instances as part of activation/deactivation.
- `GAgentBase<TState>` defers lifecycle-aware module initialization until after event-sourced state replay, so module startup observes restored committed state rather than pre-replay defaults.

## Non-goals for Phase 1

- No OpenAI realtime transport implementation.
- No ExternalLink binding from microphone/speaker transport into `VoicePresenceModule`.
- No provider callback to self-dispatch bridge yet.
- No AGUI or Host-level realtime session wiring.
- No query/read-model surface for voice sessions.

## Verification

- `dotnet build aevatar.foundation.slnf --nologo`
- `dotnet test aevatar.foundation.slnf --nologo --no-build`
- `bash tools/ci/test_stability_guards.sh`
- `bash tools/ci/solution_split_guards.sh`

`bash tools/ci/solution_split_test_guards.sh` currently fails in existing test `Aevatar.Hosting.Tests.MainnetHealthEndpointsTests.MainnetHost_ShouldExposeHealthEndpoints_AndDocumentThemInOpenApi` because the test environment cannot connect to Redis. That failure is outside the phase-1 VoicePresence change set.
