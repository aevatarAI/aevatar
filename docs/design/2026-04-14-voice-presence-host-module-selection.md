# VoicePresence Host Module Selection

## Scope

This follow-up removes the remaining host-side ambiguity after the module-factory work.

One actor can now attach multiple voice-presence modules such as `voice_presence_openai` and `voice_presence_minicpm`, but the host resolver previously exposed only two behaviors:

- return the single voice module when exactly one exists
- otherwise fall back to the default `voice_presence` alias

That meant WebSocket and WHIP hosts could not explicitly choose a non-default provider-backed voice module for the same actor. This change adds narrow request-level module selection without introducing any host-level session registry.

## Delivered

- Added `VoicePresenceSessionRequest` as the strong-typed host resolver request.
- `IVoicePresenceSessionResolver` now resolves from `VoicePresenceSessionRequest` instead of a bare `actorId` string.
- `InProcessActorVoicePresenceSessionResolver` now:
  - resolves the requested module alias when `ModuleName` is present
  - preserves the existing default `voice_presence` fallback when no module name is supplied
  - returns `null` when the requested alias is not attached to the actor
- DI-backed `MapVoicePresenceWebSocket(...)` and `MapVoicePresenceWhip(...)` now build `VoicePresenceSessionRequest` from:
  - required route value `actorId`
  - optional route value `moduleName`
  - optional query parameter `module`

## Tests

- Extended `VoicePresenceSessionResolverTests` for:
  - explicit alias selection
  - missing requested alias fallback to `null`
- Extended `VoicePresenceEndpointsTests` and `VoicePresenceWhipEndpointsTests` to verify DI-backed resolvers receive the requested module alias from the HTTP request.

## Non-goals

- No remote/runtime-neutral transport attachment beyond the existing in-process resolver boundary.
- No change to the transport protocol itself; module selection only affects which attached `VoicePresenceModule` the host resolves.

## Verification

- `dotnet test test/Aevatar.Foundation.VoicePresence.Tests/Aevatar.Foundation.VoicePresence.Tests.csproj --nologo`
- `bash tools/ci/test_stability_guards.sh`
- `dotnet build aevatar.foundation.slnf --nologo`
- `dotnet test aevatar.foundation.slnf --nologo --no-build`
