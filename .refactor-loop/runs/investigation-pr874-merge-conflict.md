# PR #874 Merge Conflict Investigation

Date: 2026-05-23

## Scope

- PR: #874
- Title: iter43 issue-865 streaming-proxy-room-chat-host-orchestration: reuse StreamingProxyGAgent + 删 coordinator/side-store
- Branch: `refactor/iter43-cluster-043-streaming-proxy-room-chat-host-orchestration`
- Base: `origin/auto-refact-dev`
- Worktree: `/Users/auric/aevatar-wt-iter43-cluster-043-streaming-proxy-room-chat-host-orchestration`

## Commands

- `git worktree list | grep iter43`
- `git fetch origin`
- `git diff origin/auto-refact-dev...HEAD --name-only`
- `gh pr view 874 --json number,title,headRefName,baseRefName,mergeable,statusCheckRollup,url`
- `git merge origin/auto-refact-dev`
- `git merge --abort`

## GitHub State

- `mergeable`: `CONFLICTING`
- `statusCheckRollup`: `[]`
- Result: no CI checks are attached to the PR.

## PR Branch Changed Files

```text
agents/Aevatar.GAgents.StreamingProxy/ServiceCollectionExtensions.cs
agents/Aevatar.GAgents.StreamingProxy/StreamingProxyEndpoints.cs
agents/Aevatar.GAgents.StreamingProxy/StreamingProxyGAgent.cs
agents/Aevatar.GAgents.StreamingProxy/StreamingProxyNyxParticipantCoordinator.cs
agents/Aevatar.GAgents.StreamingProxy/StreamingProxyRoomCredentialHandles.cs
agents/Aevatar.GAgents.StreamingProxy/StreamingProxyRoomInteraction.cs
agents/Aevatar.GAgents.StreamingProxy/StreamingProxyRoomParticipantsProjector.cs
agents/Aevatar.GAgents.StreamingProxy/StreamingProxyRoomParticipantsQueryPort.cs
agents/Aevatar.GAgents.StreamingProxy/StreamingProxyRoomParticipantsSnapshot.Partial.cs
agents/Aevatar.GAgents.StreamingProxy/StreamingProxyRoomParticipantsSnapshotMetadataProvider.cs
agents/Aevatar.GAgents.StreamingProxy/streaming_proxy_messages.proto
src/Aevatar.Studio.Application/Studio/Abstractions/IStreamingProxyParticipantStore.cs
src/Aevatar.Studio.Infrastructure/ActorBacked/ActorBackedStreamingProxyParticipantStore.cs
test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj
test/Aevatar.AI.Tests/StreamingProxyCoverageTests.cs
test/Aevatar.AI.Tests/StreamingProxyEndpointsCoverageTests.cs
test/Aevatar.AI.Tests/StreamingProxyNyxParticipantCoordinatorTests.cs
test/Aevatar.Tools.Cli.Tests/ActorBackedStoreAdapterTests.cs
```

## Merge Conflict Files

`git merge origin/auto-refact-dev` produced content conflicts in:

```text
agents/Aevatar.GAgents.StreamingProxy/StreamingProxyEndpoints.cs
agents/Aevatar.GAgents.StreamingProxy/StreamingProxyGAgent.cs
agents/Aevatar.GAgents.StreamingProxy/StreamingProxyNyxParticipantCoordinator.cs
agents/Aevatar.GAgents.StreamingProxy/StreamingProxyRoomInteraction.cs
test/Aevatar.AI.Tests/StreamingProxyCoverageTests.cs
test/Aevatar.AI.Tests/StreamingProxyEndpointsCoverageTests.cs
test/Aevatar.AI.Tests/StreamingProxyNyxParticipantCoordinatorTests.cs
```

## Conflict Classification

Escalate. The conflicts are not mechanical import-order or formatting conflicts.

The conflict is between two business-level changes in the Streaming Proxy chat path:

- PR #874 / iter43 moves room-chat progression into `StreamingProxyGAgent`, using typed room chat commands, credential handles, actor-owned participant admission, reply generation, participant leave handling, and terminal-state publication.
- `origin/auto-refact-dev` now contains later lifecycle changes around `StreamingProxyChatLifecycleFacade`, facade-owned endpoint composition, subscription attachment, participant listing, delete/join lifecycle results, and facade-related tests.

Representative conflicts:

- `StreamingProxyEndpoints.HandleChatAsync` conflicts between `ICommandInteractionService<StreamingProxyRoomChatCommand, ...>` with `IStreamingProxyRoomCredentialHandleStore` versus `StreamingProxyChatLifecycleFacade.RunChatAsync(...)`.
- `StreamingProxyGAgent.HandleChatRequest` / `HandleRoomChatRequested` conflicts between actor-owned Nyx participant orchestration and base lifecycle accepted event / `ContinueParticipantLifecycleAsync(...)`.
- `StreamingProxyNyxParticipantCoordinator` conflicts between adapter-only participant resolution / reply generation and base-side dispatch helpers that still send join/message/leave events via `IActorDispatchPort`.
- Coverage tests conflict on the expected ownership boundary: direct typed command/credential-handle behavior versus facade lifecycle behavior and facade-owned failure/cancellation assertions.

## Handling Result

- No conflict files were resolved.
- No business code was changed.
- The attempted merge was aborted with `git merge --abort`.
- This report was added as the investigation artifact.

## Suggested Next Action

Make an explicit architecture decision before resolving:

1. Keep the iter43 direction: `StreamingProxyGAgent` owns room-chat participant orchestration, and `StreamingProxyChatLifecycleFacade` should become an endpoint/application adapter that submits the typed command and observes projection output.
2. Or keep the base iter47 facade-centered lifecycle shape and rework PR #874 to remove the duplicate actor-owned orchestration.

Given the repository architecture rules, option 1 appears more aligned with actor-owned runtime state and command/event progression, but merging it requires intentionally adapting the newer facade files and tests rather than picking one side mechanically.

