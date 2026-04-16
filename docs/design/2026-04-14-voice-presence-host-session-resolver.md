# VoicePresence Host Session Resolver

## Scope

The transport phases added WebSocket and WHIP endpoints, but hosts still had to provide a handwritten `actorId -> VoicePresenceSession` delegate. This change adds a standard resolver contract plus a default in-process implementation so voice endpoints can resolve actor-scoped voice sessions directly from DI.

## Delivered

- Added `IVoicePresenceSessionResolver` in `Aevatar.Foundation.VoicePresence.Hosting`.
- Added `InProcessActorVoicePresenceSessionResolver`.
  - Resolves one actor through `IActorRuntime`.
  - Reads attached dynamic modules through the new `IEventModuleContainer<IEventHandlerContext>` abstraction.
  - Selects the single `VoicePresenceModule`, or the default `voice_presence` module when multiple voice modules are present.
  - Builds self-dispatch envelopes and routes control/provider events back through `IActorDispatchPort`.
- Added convenience endpoint overloads:
  - `MapVoicePresenceWebSocket(pattern)`
  - `MapVoicePresenceWhip(pattern, transportFactory?)`
  These now resolve `IVoicePresenceSessionResolver` from request DI automatically.
- `VoicePresenceModule` now exposes `PcmSampleRateHz` so host transports do not need to guess codec configuration.
- `GAgentBase` now implements `IEventModuleContainer<IEventHandlerContext>`.
- `AddAevatarAIFeatures(...)` now registers the default in-process session resolver whenever voice presence modules are enabled.

## Tests

- Added `VoicePresenceSessionResolverTests` covering:
  - successful session resolution
  - self-dispatch envelope routing
  - default-module selection when multiple voice modules are attached
  - no-session fallback when the actor has no voice module
- Extended `VoicePresenceEndpointsTests` and `VoicePresenceWhipEndpointsTests` to cover DI-backed resolver overloads.
- Extended `AIFeatureBootstrapCoverageTests` to verify resolver registration.

## Non-goals

- No Orleans-safe remote transport attachment yet. The default resolver is intentionally named `InProcessActorVoicePresenceSessionResolver` because it requires the resolved actor activation to expose the real module instance in-process.
- No host-level registry or actorId-to-context cache was introduced.

## Validation

- `dotnet test test/Aevatar.Foundation.VoicePresence.Tests/Aevatar.Foundation.VoicePresence.Tests.csproj --nologo`
- `dotnet test test/Aevatar.Bootstrap.Tests/Aevatar.Bootstrap.Tests.csproj --nologo --filter AIFeatureBootstrapCoverageTests`
- `bash tools/ci/test_stability_guards.sh`
