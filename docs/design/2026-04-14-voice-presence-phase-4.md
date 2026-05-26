# VoicePresence Phase 4

## Scope

This document records the repository-side implementation boundary for phase 4 of issue `#179`.

Phase 4 completes provider-side function-call execution for voice sessions and closes the remaining phase-3 control-dispatch gap for user transport control frames. It does not yet implement event-injection buffering, MiniCPM-o provider support, or WebRTC transport.

## Delivered

### Voice tool execution

- Added `IVoiceToolInvoker` as the narrow voice-session tool execution port in `Aevatar.Foundation.VoicePresence.Abstractions`.
- `VoicePresenceModule` now executes `VoiceFunctionCallRequested` events inside the actor turn and sends the resulting JSON back through `IRealtimeVoiceProvider.SendToolResultAsync(...)`.
- Tool execution is bounded by `VoicePresenceModuleOptions.ToolExecutionTimeout` and normalizes three failure modes into provider-visible error JSON:
  - no tool invoker registered
  - tool execution exception
  - tool execution timeout

### AI adapter

- Added `AgentToolVoiceInvoker` in `Aevatar.AI.Core`.
- The adapter discovers `IAgentTool` instances from the existing `IAgentToolSource` ecosystem and exposes them through `IVoiceToolInvoker`.
- Discovery is cached after the first lookup so repeated voice tool calls do not rediscover the same tool set on every turn.

### Host/relay hardening

- `VoicePresenceModule.AttachTransport(...)` now dispatches both provider control events and user transport control frames through the same self-dispatch path, so drain ACK and provider state transitions are both processed on the actor turn.
- `VoicePresenceEndpoints` now rejects a second transport before upgrading the request when the module already has an attached transport.
- If a race still causes attach failure after WebSocket upgrade, the endpoint closes the socket without detaching the previously attached transport.

### Bootstrap wiring

- `AddAevatarAIFeatures(...)` now registers `IVoiceToolInvoker -> AgentToolVoiceInvoker` so hosts that already enable AI features automatically expose the existing tool catalog to voice sessions.

## Non-goals for Phase 4

- No event-injection fence/buffer integration yet.
- No `IRealtimeVoiceProvider` text/event injection API yet.
- No MiniCPM-o provider implementation yet.
- No WebRTC transport or WHIP endpoint yet.

## Verification

- `dotnet test test/Aevatar.Foundation.VoicePresence.Tests/Aevatar.Foundation.VoicePresence.Tests.csproj --nologo`
- `dotnet test test/Aevatar.AI.Core.Tests/Aevatar.AI.Core.Tests.csproj --nologo`
- `dotnet test test/Aevatar.Bootstrap.Tests/Aevatar.Bootstrap.Tests.csproj --nologo --filter AIFeatureBootstrapCoverageTests`
- `bash tools/ci/test_stability_guards.sh`
- `dotnet build aevatar.foundation.slnf --nologo`
- `dotnet build aevatar.ai.slnf --nologo`
