# VoicePresence Tool Catalog

## Scope

This follow-up closes the remaining bootstrap gap after the module-factory and host-session-resolver work: provider-side voice tool registration no longer depends on manually duplicating tool names in `VoiceSessionConfig`.

## Delivered

- Added `IVoiceToolCatalog` to `Aevatar.Foundation.VoicePresence.Abstractions`.
- Added `VoiceToolDefinition` to the protobuf contract and extended `VoiceSessionConfig` with structured `tool_definitions`.
- Added `AgentToolVoiceCatalog`, which projects `IAgentToolSource` discovery into structured voice tool definitions.
- `AddAevatarAIFeatures(...)` now registers both:
  - `IVoiceToolInvoker -> AgentToolVoiceInvoker`
  - `IVoiceToolCatalog -> AgentToolVoiceCatalog`
- `VoicePresenceModule.InitializeAsync(...)` now merges discovered tool definitions into the configured session before calling `IRealtimeVoiceProvider.UpdateSessionAsync(...)`.
- `OpenAIRealtimeProvider` now prefers structured tool definitions, preserving tool descriptions and JSON schemas when present, and falls back to legacy `tool_names` with the permissive schema only for names that still lack structured definitions.
- `MiniCPMRealtimeProvider` now treats either `tool_names` or `tool_definitions` as the same unsupported provider-side registration capability.

## Tests

- Added `AgentToolVoiceCatalogTests`.
- Extended `VoicePresenceModuleTests` to verify discovered tool definitions are merged into the provider session config.
- Extended `OpenAIRealtimeProviderTests` to verify structured definitions are registered before legacy tool names and preserve JSON schema.
- Extended `VoicePresenceProtoTests` and `AIFeatureBootstrapCoverageTests` for the new proto field and DI registration.

## Non-goals

- No runtime-neutral remote transport attachment beyond the in-process session resolver.
- No automatic filtering of voice-callable tools beyond what existing `IAgentToolSource` registrations already expose.

## Validation

- `dotnet test test/Aevatar.AI.Core.Tests/Aevatar.AI.Core.Tests.csproj --nologo --filter AgentToolVoice`
- `dotnet test test/Aevatar.Foundation.VoicePresence.Tests/Aevatar.Foundation.VoicePresence.Tests.csproj --nologo`
- `dotnet test test/Aevatar.Foundation.VoicePresence.OpenAI.Tests/Aevatar.Foundation.VoicePresence.OpenAI.Tests.csproj --nologo`
- `dotnet test test/Aevatar.Bootstrap.Tests/Aevatar.Bootstrap.Tests.csproj --nologo --filter AIFeatureBootstrapCoverageTests`
