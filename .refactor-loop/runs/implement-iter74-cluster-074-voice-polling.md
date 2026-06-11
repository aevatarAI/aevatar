# implement-iter74-cluster-074-voice-polling

## Cluster
- id: cluster-074-voice-ws-request-polling-close-wait
- branch: refactor/iter74-cluster-074-voice-ws-polling
- worktree: /Users/auric/aevatar-wt-iter74-cluster-074-voice-polling

## Implementation
- Replaced VoicePresence WebSocket endpoint close wait polling with `WebSocketVoiceTransport.Completion` await.
- Replaced PolicyAware voice endpoint close wait polling with `WebSocketVoiceTransport.Completion` await, retaining the existing close-wait timeout as a maximum wait bound without periodic sleep.
- Added transport-owned completion signaling in `WebSocketVoiceTransport`, completed when receive enumeration ends, when constructed over an already closed socket, or on dispose.
- Updated close-path tests to use deterministic receive-close signaling / receive enumeration instead of millisecond timeout close waits.

## Scope
- src/Aevatar.Foundation.VoicePresence/Hosting/VoicePresenceEndpoints.cs
- src/Aevatar.Foundation.VoicePresence/Transport/WebSocketVoiceTransport.cs
- src/Aevatar.Mainnet.Host.Api/Voice/PolicyAwareVoiceEndpoints.cs
- test/Aevatar.Foundation.VoicePresence.Tests/VoicePresenceEndpointsTests.cs
- test/Aevatar.Foundation.VoicePresence.Tests/VoicePresenceWebSocketTestSupport.cs
- test/Aevatar.Foundation.VoicePresence.Tests/WebSocketVoiceTransportTests.cs
- test/Aevatar.ChatRouting.Voice.Integration.Tests/PolicyAwareVoiceEndpointsTests.cs

No SCOPE_EXTEND was needed beyond the requested transport/session and referenced tests.

## Verification
- `dotnet build aevatar.slnx --nologo` passed. Existing warnings only.
- `dotnet test test/Aevatar.Foundation.VoicePresence.Tests/Aevatar.Foundation.VoicePresence.Tests.csproj --nologo --filter "FullyQualifiedName~VoicePresenceEndpointsTests|FullyQualifiedName~WebSocketVoiceTransportTests"` passed: 19/19.
- `dotnet test test/Aevatar.ChatRouting.Voice.Integration.Tests/Aevatar.ChatRouting.Voice.Integration.Tests.csproj --nologo --filter FullyQualifiedName~PolicyAwareVoiceEndpointsTests` passed: 16/16.
- `dotnet test aevatar.slnx --nologo` passed. Existing skips only.
- `bash /Users/auric/aevatar/tools/ci/test_stability_guards.sh` passed before test comments were added; after final edits, worktree-local `bash tools/ci/test_stability_guards.sh` passed.
- `bash /Users/auric/aevatar/tools/ci/architecture_guards.sh` passed.
- `bash tools/ci/architecture_guards.sh` passed.

## Notes
- The root-path stability guard scans `/Users/auric/aevatar`, not this worktree. The worktree-local stability guard was run after final edits to verify the actual changed files.
- Required production comments retain the literal old pattern; duplicate test comments were removed because the stability guard scans comments for `Task.Delay(`.

IMPLEMENT_DONE:cluster-074-voice-ws-request-polling-close-wait:ok
