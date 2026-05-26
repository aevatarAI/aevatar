# VoicePresence Module Factory

## Scope

This document records the repository-side follow-up after phases 1-7 of issue `#179`.

The original phase plan delivered provider, transport, and host endpoint pieces, but `VoicePresenceModule` was still not reachable through the standard `RoleGAgent` event-module composition path. This change closes that gap by adding a standard `IEventModuleFactory<IEventHandlerContext>` implementation and bootstrap wiring for configured voice providers.

## Delivered

### Standard module factory

- Added `VoicePresenceModuleRegistration` in `Aevatar.Foundation.VoicePresence`.
- Added `VoicePresenceModuleFactory` as the standard `IEventModuleFactory<IEventHandlerContext>` implementation for voice-presence modules.
- The factory builds a case-insensitive module-name map and rejects duplicate aliases at startup.

### AI bootstrap wiring

- `AevatarAIFeatureOptions` now exposes `VoicePresence` options.
- `AddAevatarAIFeatures(...)` now registers `VoicePresenceModuleFactory` when at least one realtime voice provider is configured.
- Bootstrap registers provider-backed module aliases:
  - `voice_presence` for the resolved default provider
  - `voice_presence_openai`
  - `voice_presence_minicpm`
  - `voice_presence_minicpm_o`
- OpenAI voice module registration resolves API key from:
  - explicit `VoicePresence.OpenAIProvider.ApiKey`
  - `Aevatar:VoicePresence:OpenAI:ApiKey`
  - `OPENAI_API_KEY`
  - fallback `AevatarAIFeatureOptions.ApiKey`
- MiniCPM voice module registration resolves endpoint from:
  - explicit `VoicePresence.MiniCPMProvider.Endpoint`
  - `Aevatar:VoicePresence:MiniCPM:Endpoint`

### Runtime behavior

- Every module creation returns a fresh `VoicePresenceModule` plus a fresh provider instance, so voice sessions remain actor-scoped and do not share provider runtime state across agents.
- Session/provider configuration is cloned per module creation, so bootstrap configuration stays immutable after service-provider construction.

## Tests

- Added `VoicePresenceModuleFactoryTests` covering:
  - alias-based module creation
  - unknown-name rejection
  - duplicate-name rejection
- Extended `AIFeatureBootstrapCoverageTests` covering:
  - OpenAI-backed voice module registration
  - MiniCPM-backed default-alias registration

## Non-goals

- No host/runtime-neutral `actorId -> VoicePresenceSession` resolver yet.
- No automatic inference of provider-side tool names from agent tool sources yet.
- No change to the existing WebSocket/WHIP endpoint contract.

## Verification

- `dotnet test test/Aevatar.Foundation.VoicePresence.Tests/Aevatar.Foundation.VoicePresence.Tests.csproj --nologo`
- `dotnet test test/Aevatar.Bootstrap.Tests/Aevatar.Bootstrap.Tests.csproj --nologo --filter AIFeatureBootstrapCoverageTests`
- `bash tools/ci/test_stability_guards.sh`
- `dotnet build aevatar.foundation.slnf --nologo`
- `dotnet build aevatar.ai.slnf --nologo`
- `bash tools/ci/solution_split_guards.sh`
