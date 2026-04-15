# VoicePresence Remote Session Bridge

## Scope

The earlier host resolver work only supported in-process attachment: the host had to resolve the real `VoicePresenceModule` instance from the current activation and bind WebSocket or WHIP transports directly to that module.

That approach does not survive runtime boundaries. Once the actor activation is remote, host code cannot reach the module instance safely, and the repository rules explicitly forbid reintroducing a host-side `actorId -> session/module` registry as a workaround.

This change adds a runtime-neutral remote bridge for voice transports while keeping the authority boundary inside the actor.

## Delivered

- Added `CompositeVoicePresenceSessionResolver` as the default resolver:
  - prefer `InProcessActorVoicePresenceSessionResolver` when the activation is local
  - fall back to `RemoteActorVoicePresenceSessionResolver` when only runtime-level ports are available
- Added a remote bridge contract in `voice_presence.proto`:
  - `VoiceRemoteSessionOpenRequested`
  - `VoiceRemoteSessionCloseRequested`
  - `VoiceRemoteAudioInputReceived`
  - `VoiceRemoteControlInputReceived`
  - `VoiceRemoteTransportOutput`
  - `VoiceRemoteSessionClosed`
- Added `VoiceModuleSignal` so host-originated self/direct messages are explicitly targeted at one voice module alias.
  This avoids the earlier ambiguity where one actor could host multiple voice modules but provider/control signals had no module discriminator.
- `RemoteActorVoicePresenceSessionResolver` now:
  - verifies actor existence through `IActorRuntime`
  - sends host input through `IActorDispatchPort`
  - observes actor-owned output through `IActorEventSubscriptionProvider`
  - keeps only short-lived attachment state inside the returned session object, not in a shared host registry
- `VoicePresenceModule` now owns remote-session lifecycle in actor state:
  - claims one `_remoteSessionId`
  - accepts remote open/close/audio/control inputs only through actor events
  - republishes outbound audio and close notifications as `VoiceRemoteTransportOutput`
  - ignores module-targeted signals for other aliases
- `VoicePresenceModuleFactory` and AI bootstrap now pass the resolved alias into the module instance, so host selection and module self-dispatch use the same stable name.

## Behavioral Notes

- Remote attachment is actor-safe but intentionally asynchronous.
  `AttachTransportAsync(...)` establishes the host bridge immediately, then asks the actor to open the remote session by event.
  Failure is reported back as `VoiceRemoteTransportOutput.session_closed`, not as a synchronous RPC-style open ACK.
- Only the actor owns remote attachment facts.
  The host bridge can observe and relay, but it does not become the source of truth for whether a voice session is active.
- Provider/control events now stay module-scoped.
  A `voice_presence_minicpm` signal cannot accidentally drive `voice_presence_openai`, even when both are attached to the same actor.

## Tests

- Added `RemoteActorVoicePresenceSessionResolverTests` covering:
  - remote open dispatch
  - actor-stream audio relay back to the transport
  - remote close cleanup
  - best-effort close dispatch without a local attachment
- Added `CompositeVoicePresenceSessionResolverTests` covering:
  - in-process resolver preference
  - remote fallback when the actor is not an in-process module container
- Extended `VoicePresenceModuleTests` for:
  - module-targeted signal isolation
  - remote input handling
  - remote output publication and close behavior
- Extended `VoicePresenceModuleFactoryTests` and bootstrap coverage tests so alias-driven module naming is pinned down.

## Non-goals

- No synchronous host-side request/reply API for voice open or close.
- No host-level `actorId -> transport/session` dictionary or shared registry.
- No change to provider semantics beyond routing them through the actor-owned remote session boundary.

## Validation

- `dotnet build src/Aevatar.Foundation.VoicePresence/Aevatar.Foundation.VoicePresence.csproj --nologo`
- `dotnet test test/Aevatar.Foundation.VoicePresence.Tests/Aevatar.Foundation.VoicePresence.Tests.csproj --nologo`
- `dotnet test test/Aevatar.Bootstrap.Tests/Aevatar.Bootstrap.Tests.csproj --nologo --filter AIFeatureBootstrapCoverageTests`
- `bash tools/ci/test_stability_guards.sh`
- `dotnet build aevatar.foundation.slnf --nologo`
- `dotnet build aevatar.ai.slnf --nologo`
- `dotnet test aevatar.foundation.slnf --nologo --no-build`
- `bash tools/ci/solution_split_guards.sh`
