# Agent Profile Authority Convergence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Merge the Actor-owned Profile management system with the NyxID runtime rollout without retaining a Host-owned second Profile content source or turn-time Ornn reads.

**Architecture:** The Profile Actor remains authoritative and projects one execution read model. A Mainnet admission manifest selects and pins a published `system/nyxid-chat` snapshot. An async binder maps the read model into an immutable conversation-owned execution binding; turn execution consumes only sealed data in that binding.

**Tech Stack:** .NET 10, C#, Protobuf, xUnit, FluentAssertions, Roslyn guard tooling, Bash CI guards.

## Global Constraints

- Profile Actor committed state is the only Profile content authority.
- Query paths read materialized read models only; no priming, replay, Actor state reads, or event-store side reads.
- Mainnet rollout artifacts contain admission pins only and never Profile instructions, routing catalog, tool policy, or skill bodies.
- Existing SHADOW/ENFORCED classification, alias, telemetry, replay, and fail-closed tool narrowing semantics remain.
- Runtime turns perform zero Ornn or other remote skill fetches.
- All new durable/internal contracts use Protobuf.
- No `Task.Delay`, polling helper, force push, rebase, or whole-tree ours/theirs resolution.

---

### Task 1: Make Routing Catalog Part Of The Authoritative Profile

**Files:**
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/Protos/AgentProfiles/agent_profiles.proto`
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/AgentProfiles/AgentProfilePolicies.cs`
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/AgentProfiles/AgentProfileDeterminism.cs`
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/AgentProfiles/AgentProfileContracts.cs`
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/AgentProfiles/IAgentProfileApplicationServices.cs`
- Modify: `src/platform/Aevatar.GAgentService.Application/AgentProfiles/AgentProfileSkillSealer.cs`
- Modify: `src/platform/Aevatar.GAgentService.Hosting/AgentProfiles/AgentProfileHttpContracts.cs`
- Modify: `src/platform/Aevatar.GAgentService.Hosting/AgentProfiles/AgentProfileHttpResults.cs`
- Modify: `src/Aevatar.AI.ToolProviders.AgentCatalog/AgentProfiles/AgentProfilesTool.cs`
- Test: `test/Aevatar.GAgentService.Tests/Abstractions/AgentProfileContractsTests.cs`
- Test: `test/Aevatar.GAgentService.Tests/Application/AgentProfileSkillSealerTests.cs`
- Test: `test/Aevatar.GAgentService.Integration.Tests/AgentProfileEndpointsTests.cs`
- Test: `test/Aevatar.AI.ToolProviders.AgentCatalog.Tests/AgentProfilesToolTests.cs`

**Interfaces:**
- Produces `AgentProfileSkillRoutingPolicy` with intent ID, routing description,
  aliases, task tool policy, and typed side-effect class.
- Adds routing policy to draft and sealed bindings and adds a recovery policy to
  Profile content/published snapshot.

- [ ] Add failing contract/API/tool tests for normalized routing fields, unique
  intent IDs/aliases, typed side effects, and policy subset rules.
- [ ] Add the Protobuf fields and regenerate through the normal build.
- [ ] Normalize, hash, validate, persist, seal, expose, and audit the new fields.
- [ ] Verify undefined enum values and broader task/recovery policies fail closed.
- [ ] Run focused Abstractions, sealer, endpoint, tool, and stability tests.

### Task 2: Replace Runtime Profile Authority With An Immutable Execution Binding

**Files:**
- Modify: `src/Aevatar.AI.Abstractions/ai_messages.proto`
- Modify: `src/Aevatar.AI.Core/AgentProfiles/AgentProfileExecutionBindingCodec.cs`
- Delete: `src/Aevatar.AI.Core/AgentProfiles/IExactRemoteSkillFetcher.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/AgentProfiles/AgentProfileTurnCatalogMaterializer.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatGAgent.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/ServiceCollectionExtensions.cs`
- Modify: `src/Aevatar.AI.ToolProviders.Ornn/ServiceCollectionExtensions.cs`
- Delete: `src/Aevatar.AI.ToolProviders.Ornn/OrnnExactRemoteSkillFetcher.cs`
- Test: `test/Aevatar.AI.Tests/AgentProfileExecutionBindingCodecTests.cs`
- Test: `test/Aevatar.AI.Tests/AgentProfileTurnCatalogMaterializerTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatGAgentTests.cs`
- Test: `test/Aevatar.AI.Tests/RoleGAgentReplayContractTests.cs`
- Delete or migrate: `test/Aevatar.AI.ToolProviders.Ornn.Tests/OrnnExactRemoteSkillFetcherTests.cs`

**Interfaces:**
- Replaces `AgentProfileSnapshot` with `AgentProfileExecutionBinding` carrying
  source Profile ID/state version/revision/digest, rollout provenance, effective
  policies, and sealed routing members.
- Materialization no longer accepts an access token or remote fetcher.

- [ ] Add failing tests proving binding provenance persistence/replay and zero
  runtime remote reads in SHADOW and ENFORCED.
- [ ] Define the execution binding/member messages and deterministic digest.
- [ ] Map profile instructions and selected sealed instructions into prompt
  layers without reparsing `SKILL.md`.
- [ ] Preserve alias/classifier and strict tool intersection behavior.
- [ ] Remove remote fetch contracts, DI registration, timeout and diagnostics.
- [ ] Run AI runtime, NyxID Chat, replay, Ornn DI, and stability tests.

### Task 3: Add Read-Model-Backed NyxID Conversation Binding

**Files:**
- Replace: `agents/Aevatar.GAgents.NyxidChat/AgentProfiles/INyxIdChatAgentProfileSnapshotSource.cs`
- Replace: `agents/Aevatar.GAgents.NyxidChat/AgentProfiles/DisabledNyxIdChatAgentProfileSnapshotSource.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatLifecycleFacade.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/ServiceCollectionExtensions.cs`
- Replace: `src/Aevatar.Mainnet.Host.Api/Profiles/MainnetAgentProfileRolloutSelector.cs`
- Replace: `src/Aevatar.Mainnet.Host.Api/AgentProfiles/MainnetNyxIdChatAgentProfileSnapshotSource.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/AgentProfiles/NyxIdChatAgentProfileOptions.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/AgentProfiles/NyxIdChatAgentProfileOptionsValidator.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/appsettings.json`
- Test: `test/Aevatar.Capabilities.Tests/MainnetAgentProfileRolloutSelectorTests.cs`
- Test: `test/Aevatar.Capabilities.Tests/NyxIdChatAgentProfileOptionsTests.cs`
- Test: `test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatProfileActivationModeTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatGAgentTests.cs`

**Interfaces:**

```csharp
public enum NyxIdChatAgentProfileBindingStatus
{
    NotSelected = 0,
    Bound = 1,
    ProfileUnavailable = 2,
    AdmissionMismatch = 3,
}

public sealed record NyxIdChatAgentProfileBindingResult(
    NyxIdChatAgentProfileBindingStatus Status,
    AgentProfileExecutionBinding? Binding);

public interface INyxIdChatAgentProfileBindingSource
{
    Task<NyxIdChatAgentProfileBindingResult> ResolveForNewConversationAsync(
        string actorId,
        string routeToolSetName,
        CancellationToken ct = default);
}
```

- [ ] Add failing lifecycle tests for `NotSelected`, `Bound`, stale/missing read
  model, bad digest, namespace mismatch, closure mismatch, and no Actor creation
  on selected failures.
- [ ] Split rollout selection from runtime binding; selector returns admission
  only and hashes stable release/stage/cohort salt.
- [ ] Resolve human reference through namespace read model, then read execution
  snapshot once by opaque Profile ID and verify every source/admission pin.
- [ ] Map the complete immutable execution binding and carry it in the create
  command; never query after Actor creation.
- [ ] Register only the read-model-backed binder in Mainnet and the typed
  disabled binder in non-Mainnet hosts.
- [ ] Run capabilities, lifecycle, query-boundary, and stability tests.

### Task 4: Make Rollout Spec Parsing Portable

**Files:**
- Modify: `tools/Aevatar.Tools.AgentProfileRollout/AgentProfileRolloutCommands.cs`
- Modify: `tools/Aevatar.Tools.AgentProfileRollout/Aevatar.Tools.AgentProfileRollout.csproj`
- Rename: `src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/reviewed-release.textproto`
  to `src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/reviewed-release.json`
- Modify: `test/Aevatar.AI.ToolProviders.Ornn.Tests/AgentProfileRolloutProvisioningTests.cs`

**Interfaces:**
- `ReadReleaseSpecAsync` parses strict ProtoJSON with
  `Google.Protobuf.JsonParser` into `AgentProfileRolloutReleaseSpec`.
- Runtime no longer resolves or starts a `protoc` executable.

- [ ] Change the existing no-global-compiler test to fail before implementation
  by asserting provisioning succeeds with `PATH` excluding `protoc`.
- [ ] Convert the checked-in reviewed release to canonical ProtoJSON.
- [ ] Delete runtime compiler discovery/process execution and the custom MSBuild
  property that exports packaged `protoc`.
- [ ] Preserve malformed/unknown field rejection and exact provisioning output.
- [ ] Run all rollout provisioning and Ornn tests on the host architecture.

### Task 5: Lock The Authority Boundary With Docs And Roslyn Guards

**Files:**
- Modify: `tools/ci/Aevatar.AgentProfileBoundaryGuard.Tool/AgentProfileAuthoritySyntaxChecker.cs`
- Modify: `test/Aevatar.Architecture.Tests/AgentProfileAuthoritySyntaxCheckerTests.cs`
- Modify: `tools/ci/agent_profile_boundary_guard.sh`
- Modify: `docs/canon/agent-profiles.md`
- Modify: `docs/canon/agent-profile-rollout.md`
- Modify: `docs/canon/nyxid-chat-agent-profile-binding.md`
- Modify: `tools/eval/nyxid-chat-profile-rollout-matrix.md`

**Interfaces:**
- Roslyn checks artifact and runtime binding ownership by exact type/symbol, not
  comments or substring matches.

- [ ] Add failing compilable guard fixtures for an artifact implementing the
  runtime source, binder event-store/priming dependencies, and turn remote fetch.
- [ ] Add legal fixtures for the namespace/execution query binder and decoy
  strings/comments/local functions.
- [ ] Implement exact symbol checks and keep guard diagnostics stable.
- [ ] Update canon and rollout matrix to the Actor authority/read-model
  query/bound Actor flow and remove runtime Ornn language.
- [ ] Run boundary self-tests, architecture guards, docs lint, projection guards,
  solution ownership guards, full build, and full solution tests.
