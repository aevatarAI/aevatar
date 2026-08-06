# Owner-Aware Agent Key Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make canonical `DELETE /api/schedules/{scheduleId}` revoke a Studio member automation's scheduled-invocation Agent Key and Vault secret, support exact DELETE replay while either revocation track is pending, and reject semantic replay drift.

**Architecture:** `ScheduledDispatchGAgent` remains the sole authority for delete identity, reason, tombstone, effect fencing, and revocation state. The canonical Host derives authenticated NyxID owner/binding and a fresh bearer from `HttpContext`, then calls the existing Studio Application rich-delete port; generic and owner-only schedules retain their existing simple delete paths. The retired nested Team automation CRUD/action routes stay absent, and exact canonical DELETE replay replaces the former public retry-revocation concept.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, Protobuf actor state/events, xUnit, FluentAssertions.

## Global Constraints

- Implement from exact base `origin/feature/integrate` commit `5bf1a74773e3b880d0030ffee6f2bef0fa7d0825`.
- Use branch `fix/2026-07-27_owner-schedule-agent-key-cleanup` in an isolated worktree.
- Keep `/api/schedules` as the only schedule CRUD/action HTTP surface; nested Studio Team automation HTTP remains preflight-only.
- Keep `ScheduledDispatchGAgent` as the only authoritative owner of delete/revocation facts; Host and Application must not write actor state or read models.
- Persist new actor facts with Protobuf only.
- Do not add a public retry-revocation route and do not call `RetryRevocationAsync` from canonical DELETE replay.
- Do not accept authenticated owner, binding ID, bearer, raw key, API-key ID, Vault reference, permission digest, or other credential material from the DELETE body.
- Require `operationId` and `idempotencyKey` together for credential-aware deletion; when both are absent, preserve generic and owner-only delete behavior.
- Exact replay uses the same normalized owner tuple, `operationId`, `idempotencyKey`, and reason; only the transient authenticated bearer may change.
- Treat `202 Accepted` as admission only and return only non-secret receipt fields.
- Keep `memberId=m-alpha`, `workflowId=wf-alpha`, `publishedServiceId=svc-alpha`, and `scheduleId=sch-alpha` distinct in tests.
- Do not add polling, `Task.Delay`, query-time replay, projection priming, process-local lifecycle registries, or direct NyxID/Vault cleanup in Host code.
- Update architecture/canon documentation and pass build, focused tests, stability guards, architecture guards, and docs lint before push.

## File Map

- Modify `src/platform/Aevatar.GAgentService.Core/Schedules/scheduled_dispatch_state.proto`
  - Persist the normalized credential-aware delete reason in both the deletion-request event and actor state.
- Modify `src/platform/Aevatar.GAgentService.Core/Schedules/ScheduledDispatchGAgent.cs`
  - Include reason in exact replay identity and conflict any deleted-state payload drift before a new delete can start.
- Modify `test/Aevatar.Workflow.Core.Tests/ScheduledDispatchGAgentTests.cs`
  - Prove persisted reason, exact replay, and reason-drift conflict.
- Modify `src/Aevatar.Studio.Application.Abstractions/Provisioning/StudioMemberWorkflowScheduleContracts.cs`
  - Add optional caller reason to the existing action command without widening the port.
- Modify `src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs`
  - Propagate the normalized reason into the existing rich delete and preserve the historical fallback for internal callers.
- Modify `test/Aevatar.Studio.Tests/StudioMemberWorkflowSchedulePortTests.cs`
  - Prove rich delete, reason propagation, fresh-bearer replay, pending-track selection, and no retry-port call.
- Create `src/platform/Aevatar.GAgentService.Hosting/Endpoints/Schedules/StudioMemberAutomationHttpAuthorityResolver.cs`
  - Resolve trusted NyxID subject, verified external binding, and bearer from `HttpContext`.
- Modify `src/Aevatar.Studio.Hosting/Endpoints/StudioMemberAutomationEndpoints.cs`
  - Make the sole mapped nested preflight endpoint use the shared resolver.
- Modify `test/Aevatar.Studio.Tests/StudioMemberAutomationEndpointsTests.cs`
  - Prove shared resolver behavior and sanitized unauthorized responses.
- Modify `src/platform/Aevatar.GAgentService.Hosting/Endpoints/Schedules/ScheduledDispatchEndpoints.cs`
  - Add the delete-specific request contract and optional Studio lifecycle branch.
- Modify `test/Aevatar.GAgentService.Integration.Tests/ScheduledDispatchEndpointsTests.cs`
  - Prove all valid branches, invalid combinations, trusted authority, error mapping, and secret-free receipts.
- Modify `docs/canon/scheduled-skill-runners.md`
  - Document canonical credential-aware DELETE and exact-body replay.

---

### Task 1: Make Delete Reason Part of the Actor-Owned Replay Identity

**Files:**

- Modify: `src/platform/Aevatar.GAgentService.Core/Schedules/scheduled_dispatch_state.proto`
- Modify: `src/platform/Aevatar.GAgentService.Core/Schedules/ScheduledDispatchGAgent.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/ScheduledDispatchGAgentTests.cs`

**Interfaces:**

- Consumes: `ScheduledDispatchDeleteCommand.Reason`.
- Produces: presence-aware `ScheduledDispatchState.TeamAutomationDeleteReason`
  and `TeamAutomationDeletionRequestedEvent.Reason`.
- Produces: existing stable conflict observation
  `team_automation_operation_conflict`.

- [ ] **Step 1: Add a failing Actor test for persisted normalized reason**

Add beside the existing Team automation delete tests:

```csharp
[Fact]
public async Task TeamAutomationDelete_ShouldPersistNormalizedReasonAsReplayIdentity()
{
    var eventStore = new TestEventStore();
    var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
    await agent.ActivateAsync();
    await ActivateTeamAutomationAsync(
        agent,
        CreateTeamCredential("key-alpha"),
        enabled: false);

    await agent.HandleDeleteAsync(new ScheduledDispatchDeleteCommand
    {
        Reason = " scheduled_agent_key_canary_cleanup ",
        TeamAutomationOwner = CreateTeamOwner(),
        OperationId = "operation-delete",
        IdempotencyKey = "idempotency-delete",
        AuthenticatedCredentialOwner = CreateCredentialOwner(),
    });

    agent.State.TeamAutomationDeleteReason.Should().Be(
        "scheduled_agent_key_canary_cleanup");
    agent.State.HasTeamAutomationDeleteReason.Should().BeTrue();
    var requested = eventStore.GetEvents(ScheduleActorId)
        .Single(x => x.EventType ==
            TeamAutomationDeletionRequestedEvent.Descriptor.FullName)
        .EventData
        .Unpack<TeamAutomationDeletionRequestedEvent>();
    requested.Reason.Should().Be("scheduled_agent_key_canary_cleanup");
    requested.HasReason.Should().BeTrue();

    var reactivated = CreateAgent(
        eventStore,
        new RecordingActorDispatchPort());
    await reactivated.ActivateAsync();
    reactivated.State.TeamAutomationDeleteReason.Should().Be(
        "scheduled_agent_key_canary_cleanup");
    reactivated.State.HasTeamAutomationDeleteReason.Should().BeTrue();
}
```

- [ ] **Step 2: Add failing pending and terminal reason-drift tests**

The observed command wrapper records stable rejection events instead of
surfacing the private Actor exception. Add:

```csharp
[Fact]
public async Task TeamAutomationDelete_ReasonDriftWhileRevocationPending_ShouldRejectConflict()
{
    var eventStore = new TestEventStore();
    var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
    await agent.ActivateAsync();
    await ActivateTeamAutomationAsync(
        agent,
        CreateTeamCredential("key-alpha"),
        enabled: false);
    var delete = new ScheduledDispatchDeleteCommand
    {
        Reason = "scheduled_agent_key_canary_cleanup",
        TeamAutomationOwner = CreateTeamOwner(),
        OperationId = "operation-delete",
        IdempotencyKey = "idempotency-delete",
        AuthenticatedCredentialOwner = CreateCredentialOwner(),
        ObservationRequestId = "delete-initial",
    };
    await agent.HandleDeleteAsync(delete);

    var drift = delete.Clone();
    drift.Reason = "different_cleanup_reason";
    drift.ObservationRequestId = "delete-reason-drift-pending";
    await agent.HandleDeleteAsync(drift);

    var rejection = eventStore.GetEvents(ScheduleActorId)
        .Where(x => x.EventType ==
            TeamAutomationOperationObservedEvent.Descriptor.FullName)
        .Select(x =>
            x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
        .Single(x => x.ObservationRequestId ==
            "delete-reason-drift-pending");
    rejection.ObservationStatus.Should().Be(
        TeamAutomationOperationObservationStatusState.RejectedConflict);
    rejection.ErrorCode.Should().Be(
        "team_automation_operation_conflict");
    rejection.OwnsEffectAttempt.Should().BeFalse();
    eventStore.GetEvents(ScheduleActorId)
        .Count(x => x.EventType ==
            TeamAutomationDeletionRequestedEvent.Descriptor.FullName)
        .Should().Be(1);
    eventStore.GetEvents(ScheduleActorId)
        .Count(x => x.EventType ==
            ScheduledDispatchDeletedEvent.Descriptor.FullName)
        .Should().Be(1);
}

[Fact]
public async Task TeamAutomationDelete_ReasonDriftAfterRevocationCompletes_ShouldRejectConflict()
{
    var eventStore = new TestEventStore();
    var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
    await agent.ActivateAsync();
    await ActivateTeamAutomationAsync(
        agent,
        CreateTeamCredential("key-alpha"),
        enabled: false);
    var delete = new ScheduledDispatchDeleteCommand
    {
        Reason = "scheduled_agent_key_canary_cleanup",
        TeamAutomationOwner = CreateTeamOwner(),
        OperationId = "operation-delete",
        IdempotencyKey = "idempotency-delete",
        AuthenticatedCredentialOwner = CreateCredentialOwner(),
        ObservationRequestId = "delete-initial",
    };
    await agent.HandleDeleteAsync(delete);
    await agent.HandleCompleteTeamAutomationRevocationAsync(
        new CompleteTeamAutomationRevocationCommand
        {
            Owner = CreateTeamOwner(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            EffectAttemptId =
                agent.State.TeamAutomationEffectAttemptId,
            NyxidRevoked = true,
            VaultRevoked = true,
        });

    var drift = delete.Clone();
    drift.Reason = "different_cleanup_reason";
    drift.ObservationRequestId = "delete-reason-drift-terminal";
    await agent.HandleDeleteAsync(drift);

    var rejection = eventStore.GetEvents(ScheduleActorId)
        .Where(x => x.EventType ==
            TeamAutomationOperationObservedEvent.Descriptor.FullName)
        .Select(x =>
            x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
        .Single(x => x.ObservationRequestId ==
            "delete-reason-drift-terminal");
    rejection.ObservationStatus.Should().Be(
        TeamAutomationOperationObservationStatusState.RejectedConflict);
    rejection.ErrorCode.Should().Be(
        "team_automation_operation_conflict");
    rejection.OwnsEffectAttempt.Should().BeFalse();
    eventStore.GetEvents(ScheduleActorId)
        .Count(x => x.EventType ==
            TeamAutomationDeletionRequestedEvent.Descriptor.FullName)
        .Should().Be(1);
    eventStore.GetEvents(ScheduleActorId)
        .Count(x => x.EventType ==
            ScheduledDispatchDeletedEvent.Descriptor.FullName)
        .Should().Be(1);
}
```

- [ ] **Step 3: Run the new tests and confirm RED**

Run:

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~TeamAutomationDelete_ShouldPersistNormalizedReasonAsReplayIdentity|FullyQualifiedName~TeamAutomationDelete_ReasonDrift"
```

Expected RED:

```text
TeamAutomationDeleteReason and TeamAutomationDeletionRequestedEvent.Reason do not exist,
or the drifted replay is recorded as committed instead of rejected conflict.
```

- [ ] **Step 4: Add the Protobuf fields**

Use the next free state and event field numbers:

```proto
message ScheduledDispatchState
{
  // Existing fields 1 through 64 remain unchanged.
  optional string team_automation_delete_reason = 65;
}

message TeamAutomationDeletionRequestedEvent
{
  TeamMemberAutomationOwnerState owner = 1;
  string operation_id = 2;
  string idempotency_key = 3;
  ScheduledInvocationAgentKeyCredentialReferenceState pending_revocation_credential = 4;
  google.protobuf.Timestamp occurred_at = 5;
  ScheduledInvocationAuthorizationOwnerState pending_revocation_credential_owner = 6;
  optional string reason = 7;
}
```

Do not renumber or reuse any existing field.

Presence is required because proto3 plain strings cannot distinguish a
pre-field snapshot/event from a new operation whose normalized reason is
empty. New writes always set the optional field, including an explicit empty
normalized reason. Legacy deleted state with `HasTeamAutomationDeleteReason =
false` fails closed as a payload conflict; an unknown historical reason must
not become a wildcard replay identity.

- [ ] **Step 5: Persist and apply the normalized reason**

At the start of `HandleDeleteCoreAsync`, normalize once:

```csharp
var normalizedReason = NormalizeOptional(command.Reason);
```

Assign the optional deletion-request field explicitly:

```csharp
Reason = normalizedReason,
```

Reuse the same value in `ScheduledDispatchDeletedEvent`:

```csharp
Reason = normalizedReason,
```

In `ApplyTeamAutomationDeletionRequested`, add:

```csharp
if (evt.HasReason)
    next.TeamAutomationDeleteReason = evt.Reason;
else
    next.ClearTeamAutomationDeleteReason();
```

In `ApplyDeleted`, recover a pre-field full event stream from the already
committed tombstone event:

```csharp
if (next.TeamAutomationOperationKind ==
        TeamAutomationOperationKindState.Delete &&
    !next.HasTeamAutomationDeleteReason)
{
    next.TeamAutomationDeleteReason =
        NormalizeOptional(evt.Reason);
}
```

This fallback works only when the historical `ScheduledDispatchDeletedEvent`
is still replayed. A compacted legacy snapshot without presence remains
unknown and must conflict.

- [ ] **Step 6: Fence Team automation deleted-state replay before other delete logic**

Replace the current completed-delete condition with:

```csharp
if (State.Deleted &&
    State.TeamAutomationOperationKind ==
        TeamAutomationOperationKindState.Delete)
{
    if (!IsSameCompletedDeleteOperation(
            command,
            normalizedReason))
    {
        throw TeamAutomationCommandRejectedException.Conflict(
            "team_automation_operation_conflict");
    }

    EnsureObservedCredentialAuthorizationOwnerAccess(
        command.AuthenticatedCredentialOwner,
        State.TeamCredentialEffectLocator?.CredentialOwner);
    await PersistTeamAutomationObservationAsync(
        TeamAutomationOperationObservationStages.Delete,
        State.PendingRevocationTeamCredential != null &&
        CanClaimTeamAutomationEffectAttempt(_timeProvider.GetUtcNow()),
        CancellationToken.None,
        observationRequestId: command.ObservationRequestId);
    return;
}
```

Extend the exact predicate:

```csharp
private bool IsSameCompletedDeleteOperation(
    ScheduledDispatchDeleteCommand command,
    string normalizedReason) =>
    State.TeamAutomationOwner != null &&
    command.TeamAutomationOwner != null &&
    TeamAutomationOwnerEquals(
        State.TeamAutomationOwner,
        command.TeamAutomationOwner) &&
    string.Equals(
        State.TeamAutomationOperationId,
        command.OperationId?.Trim(),
        StringComparison.Ordinal) &&
    string.Equals(
        State.TeamAutomationIdempotencyKey,
        command.IdempotencyKey?.Trim(),
        StringComparison.Ordinal) &&
    State.HasTeamAutomationDeleteReason &&
    string.Equals(
        State.TeamAutomationDeleteReason,
        normalizedReason,
        StringComparison.Ordinal);
```

The operation-kind guard preserves generic and simple owner-only deleted
schedule behavior. Presence is part of exact identity; a compacted legacy
snapshot cannot adopt the current request's reason.

- [ ] **Step 7: Prove full-stream legacy recovery without weakening compacted snapshots**

Add this narrow test-store helper:

```csharp
public void ClearTeamAutomationDeletionRequestedReason(
    string agentId)
{
    var stateEvent = _streams[agentId].Single(x =>
        x.EventType ==
        TeamAutomationDeletionRequestedEvent.Descriptor.FullName);
    var requested =
        stateEvent.EventData
            .Unpack<TeamAutomationDeletionRequestedEvent>();
    requested.ClearReason();
    stateEvent.EventData = Any.Pack(requested);
}
```

Add:

```csharp
[Fact]
public async Task TeamAutomationDelete_LegacyFullReplay_ShouldRecoverReasonFromDeletedEvent()
{
    var eventStore = new TestEventStore();
    var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
    await agent.ActivateAsync();
    await ActivateTeamAutomationAsync(
        agent,
        CreateTeamCredential("key-alpha"),
        enabled: false);
    var delete = new ScheduledDispatchDeleteCommand
    {
        Reason = "scheduled_agent_key_canary_cleanup",
        TeamAutomationOwner = CreateTeamOwner(),
        OperationId = "operation-delete",
        IdempotencyKey = "idempotency-delete",
        AuthenticatedCredentialOwner = CreateCredentialOwner(),
        ObservationRequestId = "delete-initial",
    };
    await agent.HandleDeleteAsync(delete);
    eventStore.ClearTeamAutomationDeletionRequestedReason(
        ScheduleActorId);

    var reactivated = CreateAgent(
        eventStore,
        new RecordingActorDispatchPort());
    await reactivated.ActivateAsync();

    reactivated.State.HasTeamAutomationDeleteReason
        .Should().BeTrue();
    reactivated.State.TeamAutomationDeleteReason.Should()
        .Be("scheduled_agent_key_canary_cleanup");
    var replay = delete.Clone();
    replay.ObservationRequestId = "delete-legacy-exact-replay";
    await reactivated.HandleDeleteAsync(replay);

    var observation = eventStore.GetEvents(ScheduleActorId)
        .Where(x => x.EventType ==
            TeamAutomationOperationObservedEvent.Descriptor.FullName)
        .Select(x =>
            x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
        .Single(x => x.ObservationRequestId ==
            "delete-legacy-exact-replay");
    observation.ObservationStatus.Should().Be(
        TeamAutomationOperationObservationStatusState.Committed);
}
```

Do not add a wildcard for an absent reason. The test proves only the
deterministic full-stream fallback.

- [ ] **Step 8: Run the focused Actor tests and confirm GREEN**

Run:

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj \
  --nologo \
  --filter ScheduledDispatchGAgentTests
```

Expected: all `ScheduledDispatchGAgentTests` pass, including the existing exact
replay and terminal no-op tests plus the two new reason tests.

- [ ] **Step 9: Commit the Actor contract**

```bash
git add \
  src/platform/Aevatar.GAgentService.Core/Schedules/scheduled_dispatch_state.proto \
  src/platform/Aevatar.GAgentService.Core/Schedules/ScheduledDispatchGAgent.cs \
  test/Aevatar.Workflow.Core.Tests/ScheduledDispatchGAgentTests.cs
git commit -m "Fix schedule delete replay identity"
```

---

### Task 2: Propagate Reason Through the Existing Studio Rich Delete

**Files:**

- Modify: `src/Aevatar.Studio.Application.Abstractions/Provisioning/StudioMemberWorkflowScheduleContracts.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs`
- Test: `test/Aevatar.Studio.Tests/StudioMemberWorkflowSchedulePortTests.cs`

**Interfaces:**

- Consumes: existing `IStudioMemberWorkflowSchedulePort.DeleteAsync(StudioMemberAutomationActionCommand, CancellationToken)`.
- Produces: `StudioMemberAutomationActionCommand.Reason`.
- Preserves: rich `IScheduledDispatchApplicationService.DeleteTeamAutomationAsync(...)`.

- [ ] **Step 1: Extend the recording fakes for two rich-delete attempts**

In `StudioMemberWorkflowSchedulePortTests`, add these recording properties:

```csharp
public List<(string BearerToken, bool RevokeNyxId, bool RevokeVault)>
    RevocationCalls { get; } = [];

public Queue<StudioScheduledCredentialRevocationResult>
    RevocationResults { get; } = [];

public List<RichDeleteCall> RichDeleteCalls { get; } = [];

public Queue<DeleteAttempt> DeleteAttempts { get; } = [];
```

At the start of the recording materializer's `RevokeAsync`:

```csharp
RevocationCalls.Add((bearerToken, revokeNyxId, revokeVault));
if (RevocationResults.Count > 0)
    return Task.FromResult(RevocationResults.Dequeue());
```

Make the recording schedule service's existing rich overload append:

```csharp
RichDeleteCalls.Add(new RichDeleteCall(
    scheduleId,
    owner,
    operationId,
    idempotencyKey,
    reason,
    authenticatedCredentialOwner));
```

Then dequeue one `DeleteAttempt` and return a committed mutation receipt whose
pending flags and `OwnsEffectAttempt` match that attempt. Define:

```csharp
private sealed record DeleteAttempt(
    bool OwnsEffectAttempt,
    bool NyxIdPending,
    bool VaultPending);

private sealed record RichDeleteCall(
    string ScheduleId,
    TeamMemberAutomationOwner Owner,
    string OperationId,
    string IdempotencyKey,
    string Reason,
    ScheduledInvocationAuthorizationOwner AuthenticatedCredentialOwner);
```

- [ ] **Step 2: Add the failing rich-delete reason test**

```csharp
[Fact]
public async Task DeleteAsync_ShouldUseRichDeleteAndPropagateReasonAndFreshAuthority()
{
    var scheduleService = new RecordingScheduleService();
    scheduleService.DeleteAttempts.Enqueue(new DeleteAttempt(
        OwnsEffectAttempt: true,
        NyxIdPending: true,
        VaultPending: true));
    var materializer = new RecordingCredentialMaterializer();
    var port = NewPort(scheduleService, materializer: materializer);
    var owner = Request("scope-1", "member-1").AuthenticatedOwner;

    var result = await port.DeleteAsync(
        new StudioMemberAutomationActionCommand(
            "scope-1",
            "team-1",
            "member-1",
            "schedule-1",
            "operation-delete",
            "idempotency-delete")
        {
            Reason = " scheduled_agent_key_canary_cleanup ",
            AuthenticatedOwner = owner,
            ProvisioningBearerToken = "fresh-bearer-sensitive",
        });

    result.Accepted.Should().BeTrue();
    result.Status.Should().Be("pending");
    var call = scheduleService.RichDeleteCalls
        .Should().ContainSingle().Subject;
    call.Owner.Should().Be(new TeamMemberAutomationOwner(
        "scope-1",
        "member-1",
        "team-1"));
    call.Reason.Should().Be("scheduled_agent_key_canary_cleanup");
    materializer.RevocationCalls.Should().ContainSingle().Which.Should().Be(
        ("fresh-bearer-sensitive", true, true));
}
```

- [ ] **Step 3: Add the failing exact replay test**

```csharp
[Fact]
public async Task DeleteAsync_ExactReplay_ShouldContinueOnlyPendingRevocationWithFreshBearer()
{
    var scheduleService = new RecordingScheduleService();
    scheduleService.DeleteAttempts.Enqueue(new DeleteAttempt(
        OwnsEffectAttempt: true,
        NyxIdPending: true,
        VaultPending: true));
    scheduleService.DeleteAttempts.Enqueue(new DeleteAttempt(
        OwnsEffectAttempt: true,
        NyxIdPending: false,
        VaultPending: true));
    var materializer = new RecordingCredentialMaterializer();
    materializer.RevocationResults.Enqueue(
        new StudioScheduledCredentialRevocationResult(
            NyxIdRevoked: true,
            VaultRevoked: false,
            ErrorCode: "credential_revocation_transient"));
    materializer.RevocationResults.Enqueue(
        new StudioScheduledCredentialRevocationResult(
            NyxIdRevoked: true,
            VaultRevoked: true,
            ErrorCode: string.Empty));
    var port = NewPort(scheduleService, materializer: materializer);
    var owner = Request("scope-1", "member-1").AuthenticatedOwner;
    var first = new StudioMemberAutomationActionCommand(
        "scope-1",
        "team-1",
        "member-1",
        "schedule-1",
        "operation-delete",
        "idempotency-delete")
    {
        Reason = "scheduled_agent_key_canary_cleanup",
        AuthenticatedOwner = owner,
        ProvisioningBearerToken = "fresh-bearer-1",
    };

    await port.DeleteAsync(first);
    await port.DeleteAsync(first with
    {
        ProvisioningBearerToken = "fresh-bearer-2",
    });

    scheduleService.RichDeleteCalls.Should().HaveCount(2);
    scheduleService.RichDeleteCalls.Should().OnlyContain(call =>
        call.ScheduleId == "schedule-1" &&
        call.OperationId == "operation-delete" &&
        call.IdempotencyKey == "idempotency-delete" &&
        call.Reason == "scheduled_agent_key_canary_cleanup");
    materializer.RevocationCalls.Should().Equal(
        ("fresh-bearer-1", true, true),
        ("fresh-bearer-2", false, true));
    scheduleService.RetryRevocationCallCount.Should().Be(0);
}
```

- [ ] **Step 4: Run the two tests and confirm RED**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj \
  --nologo \
  --filter "DeleteAsync_ShouldUseRichDeleteAndPropagateReasonAndFreshAuthority|DeleteAsync_ExactReplay_ShouldContinueOnlyPendingRevocationWithFreshBearer"
```

Expected RED: `Reason` does not exist or the recorded rich call receives
`studio_team_automation_delete` instead of the supplied normalized reason.

- [ ] **Step 5: Add reason without adding another port overload**

Change the existing action command to:

```csharp
public sealed record StudioMemberAutomationActionCommand(
    string ScopeId,
    string TeamId,
    string MemberId,
    string ScheduleId,
    string OperationId,
    string IdempotencyKey)
{
    public string? Reason { get; init; }

    public string? ProvisioningBearerToken { get; init; }

    public AuthenticatedAuthorizationOwnerContext? AuthenticatedOwner
    {
        get;
        init;
    }
}
```

Do not modify `IStudioMemberWorkflowSchedulePort`.

- [ ] **Step 6: Replace only the hardcoded rich-delete reason**

In `StudioMemberWorkflowSchedulePort.ApplyActionAsync`, use:

```csharp
var committed = await _scheduleService.DeleteTeamAutomationAsync(
    scheduleId,
    owner,
    operationId,
    idempotencyKey,
    NormalizeOptional(command.Reason) ??
        "studio_team_automation_delete",
    ToAuthorizationOwner(authenticatedOwner),
    ct);
```

The fallback preserves existing internal callers. Canonical callers pass an
explicit stable reason.

- [ ] **Step 7: Verify GREEN and secret-free receipt serialization**

Run the Task 2 focused test command again. Also serialize the first receipt
with `JsonSerializerDefaults.Web` and assert it excludes:

```csharp
foreach (var forbidden in new[]
         {
             "fresh-bearer-sensitive",
             "nyx-owner-alpha",
             "binding-alpha",
             "key-alpha",
             "secret-alpha",
             "ApiKeyId",
             "SecretReference",
             "VaultReference",
             "CallerAuthority",
             "VerifiedBindingId",
         })
{
    serialized.Should().NotContain(forbidden);
}
```

Expected: both tests pass and the serialized receipt contains only admission
facts.

- [ ] **Step 8: Commit the Studio Application change**

```bash
git add \
  src/Aevatar.Studio.Application.Abstractions/Provisioning/StudioMemberWorkflowScheduleContracts.cs \
  src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs \
  test/Aevatar.Studio.Tests/StudioMemberWorkflowSchedulePortTests.cs
git commit -m "Route replay through Studio schedule delete"
```

---

### Task 3: Share Trusted HTTP Authority Resolution

**Files:**

- Create: `src/platform/Aevatar.GAgentService.Hosting/Endpoints/Schedules/StudioMemberAutomationHttpAuthorityResolver.cs`
- Modify: `src/Aevatar.Studio.Hosting/Endpoints/StudioMemberAutomationEndpoints.cs`
- Test: `test/Aevatar.Studio.Tests/StudioMemberAutomationEndpointsTests.cs`

**Interfaces:**

- Consumes: `HttpContext`, `IExternalIdentityBindingQueryPort`.
- Produces: `StudioMemberAutomationHttpAuthority`.
- Produces: sanitized `UnauthorizedAccessException` reasons without identity or token values.

- [ ] **Step 1: Add failing preflight tests for missing binding and malformed bearer**

```csharp
[Fact]
public async Task Preflight_WhenNyxIdBindingIsMissing_ShouldReturnUnauthorizedWithoutSecrets()
{
    var schedules = new StubSchedules();
    var result = await StudioMemberAutomationEndpoints.HandlePreflightAsync(
        CreateContext(ScopeId),
        ScopeId,
        TeamId,
        MemberId,
        new StudioMemberAutomationPreflightRequest(
            "0 9 * * *",
            "UTC",
            "run daily digest",
            "Daily digest",
            true),
        schedules,
        new StubBindingQuery { Binding = null },
        CancellationToken.None);

    StatusCode(result).Should().Be(StatusCodes.Status401Unauthorized);
    StringProperty(Value(result), "code").Should().Be(
        "TEAM_AUTOMATION_UNAUTHORIZED");
    schedules.LastPreflight.Should().BeNull();
    var json = JsonSerializer.Serialize(Value(result));
    json.Should().NotContain("binding-alpha");
    json.Should().NotContain("nyx-owner-alpha");
    json.Should().NotContain("fresh-owner-bearer");
}

[Fact]
public async Task Preflight_WhenBearerIsMalformed_ShouldReturnUnauthorizedWithoutEchoingHeader()
{
    var schedules = new StubSchedules();
    var context = CreateContext(ScopeId);
    context.Request.Headers.Authorization =
        "Bearer secret-one, secret-two";

    var result = await StudioMemberAutomationEndpoints.HandlePreflightAsync(
        context,
        ScopeId,
        TeamId,
        MemberId,
        new StudioMemberAutomationPreflightRequest(
            "0 9 * * *",
            "UTC",
            "run daily digest",
            "Daily digest",
            true),
        schedules,
        new StubBindingQuery(),
        CancellationToken.None);

    StatusCode(result).Should().Be(StatusCodes.Status401Unauthorized);
    StringProperty(Value(result), "code").Should().Be(
        "TEAM_AUTOMATION_UNAUTHORIZED");
    schedules.LastPreflight.Should().BeNull();
    var json = JsonSerializer.Serialize(Value(result));
    json.Should().NotContain("secret-one");
    json.Should().NotContain("secret-two");
}
```

- [ ] **Step 2: Run the tests and confirm RED**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj \
  --nologo \
  --filter "Preflight_WhenNyxIdBindingIsMissing_ShouldReturnUnauthorizedWithoutSecrets|Preflight_WhenBearerIsMalformed_ShouldReturnUnauthorizedWithoutEchoingHeader"
```

Expected RED: missing binding currently maps as `400`, and no shared resolver
exists.

- [ ] **Step 3: Create the shared resolver**

Create the file with:

```csharp
using System.Security.Claims;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgentService.Hosting.Endpoints.Schedules;

public sealed record StudioMemberAutomationHttpAuthority(
    AuthenticatedAuthorizationOwnerContext AuthenticatedOwner,
    string ProvisioningBearerToken);

public static class StudioMemberAutomationHttpAuthorityResolver
{
    public static async Task<StudioMemberAutomationHttpAuthority> ResolveAsync(
        HttpContext http,
        IExternalIdentityBindingQueryPort bindingQuery,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(bindingQuery);

        var subject =
            http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
            http.User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
            throw new UnauthorizedAccessException("nyxid_subject_missing");

        var normalizedSubject = subject.Trim();
        var binding = await bindingQuery.ResolveAsync(
            new ExternalSubjectRef
            {
                Platform = OwnerScope.NyxIdPlatform,
                Tenant = string.Empty,
                ExternalUserId = normalizedSubject,
            },
            ct);
        if (binding == null || string.IsNullOrWhiteSpace(binding.Value))
            throw new UnauthorizedAccessException("nyxid_binding_missing");

        return new StudioMemberAutomationHttpAuthority(
            new AuthenticatedAuthorizationOwnerContext(
                new AuthorizationOwnerIdentity
                {
                    Authority = NyxIdAuthorizationAuthorities.NyxId,
                    OwnerKind = AuthorizationOwnerKind.Personal,
                    OwnerSubject = normalizedSubject,
                },
                OwnerScope.NyxIdPlatform,
                string.Empty,
                normalizedSubject,
                binding.Value.Trim()),
            ResolveBearerToken(http));
    }

    private static string ResolveBearerToken(HttpContext http)
    {
        var header =
            http.Request.Headers.Authorization.FirstOrDefault()?.Trim();
        const string prefix = "Bearer ";
        if (header == null ||
            !header.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "provisioning_bearer_missing");
        }

        var token = header[prefix.Length..].Trim();
        if (token.Length == 0 || token.Contains(','))
        {
            throw new UnauthorizedAccessException(
                "provisioning_bearer_invalid");
        }

        return token;
    }
}
```

- [ ] **Step 4: Make every retained Studio Host caller consume the helper**

Add:

```csharp
using Aevatar.GAgentService.Hosting.Endpoints.Schedules;
```

Then replace the preflight owner/bearer construction with:

```csharp
var authority =
    await StudioMemberAutomationHttpAuthorityResolver.ResolveAsync(
        http,
        bindingQuery,
        ct);
return Results.Ok(await schedules.PreflightAsync(
    BuildScheduleRequest(
        scopeId,
        teamId,
        memberId,
        body,
        authority.AuthenticatedOwner,
        authority.ProvisioningBearerToken),
    ct));
```

For each retained, currently unmapped historical handler that still compiles
in this file, replace its private owner/bearer calls with the same authority
object. Use these exact mappings:

```csharp
var authority =
    await StudioMemberAutomationHttpAuthorityResolver.ResolveAsync(
        http,
        bindingQuery,
        ct);

// BuildScheduleRequest callers:
authority.AuthenticatedOwner
authority.ProvisioningBearerToken

// StudioMemberAutomationUpdateCommand:
AuthenticatedOwner = authority.AuthenticatedOwner
ProvisioningBearerToken = authority.ProvisioningBearerToken

// StudioMemberAutomationActionCommand for delete/retry:
AuthenticatedOwner = authority.AuthenticatedOwner
ProvisioningBearerToken = authority.ProvisioningBearerToken
```

After every reference is replaced, delete private `ResolveOwnerAsync`,
private `ResolveBearerToken`, and private `ResolvedOwner`. Remove
`using System.Security.Claims` and any other imports made unused by that
deletion.

Retain the existing route map assertion that only nested `/preflight` is
exposed. Do not map any retired handler.

- [ ] **Step 5: Verify the Studio endpoint tests**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj \
  --nologo \
  --filter StudioMemberAutomationEndpointsTests
```

Expected: all endpoint tests pass, preflight still carries the fresh bearer
and verified owner, and unauthorized responses contain no secret material.

- [ ] **Step 6: Commit the shared authority boundary**

```bash
git add \
  src/platform/Aevatar.GAgentService.Hosting/Endpoints/Schedules/StudioMemberAutomationHttpAuthorityResolver.cs \
  src/Aevatar.Studio.Hosting/Endpoints/StudioMemberAutomationEndpoints.cs \
  test/Aevatar.Studio.Tests/StudioMemberAutomationEndpointsTests.cs
git commit -m "Share Studio schedule HTTP authority"
```

---

### Task 4: Add the Canonical Credential-Aware DELETE Branch

**Files:**

- Modify: `src/platform/Aevatar.GAgentService.Hosting/Endpoints/Schedules/ScheduledDispatchEndpoints.cs`
- Test: `test/Aevatar.GAgentService.Integration.Tests/ScheduledDispatchEndpointsTests.cs`

**Interfaces:**

- Consumes: `ScheduledDispatchDeleteHttpRequest`.
- Consumes: optional `IStudioMemberWorkflowSchedulePort` and `IExternalIdentityBindingQueryPort` from `HttpContext.RequestServices`.
- Produces: one of existing generic receipt or `StudioMemberAutomationMutationReceipt`.

- [ ] **Step 1: Add a failing lifecycle-path endpoint test**

Add a recording Studio port and binding query to the fixture, then add:

```csharp
[Fact]
public async Task Delete_WithStableLifecycleIdentity_ShouldUseStudioLifecyclePort()
{
    var genericSchedules =
        new RecordingScheduledDispatchApplicationService
        {
            DeleteException = new InvalidOperationException(
                "team_automation_delete_requires_revocation_context"),
        };
    var lifecycleSchedules =
        new RecordingStudioMemberWorkflowSchedulePort();
    var bindingQuery = new FakeExternalIdentityBindingQueryPort();
    bindingQuery.Bindings[
        SubjectKey(OwnerSubject("nyx-owner-alpha"))] = "binding-alpha";
    var requestHttp = CreateLifecycleDeleteHttpContext(
        lifecycleSchedules,
        bindingQuery);

    var result = await ScheduledDispatchEndpoints.Delete(
        requestHttp,
        "sch-alpha",
        null,
        new ScheduledDispatchDeleteHttpRequest
        {
            Reason = "scheduled_agent_key_canary_cleanup",
            OperationId = "delete-operation-alpha",
            IdempotencyKey = "delete-idempotency-alpha",
            Owner = StudioMemberAutomationOwnerRequest(),
        },
        genericSchedules);

    var responseHttp = CreateHttpContext();
    await result.ExecuteAsync(responseHttp);
    responseHttp.Response.Body.Position = 0;
    using var json =
        await JsonDocument.ParseAsync(responseHttp.Response.Body);

    responseHttp.Response.StatusCode.Should().Be(
        StatusCodes.Status202Accepted);
    json.RootElement.GetProperty("status").GetString()
        .Should().Be("pending");
    json.RootElement.GetProperty("operationId").GetString()
        .Should().Be("delete-operation-alpha");
    AssertNoCredentialMaterial(json.RootElement);
    genericSchedules.Deleted.Should().BeEmpty();
    genericSchedules.TeamDeleted.Should().BeEmpty();
    lifecycleSchedules.LastDelete!.Reason.Should().Be(
        "scheduled_agent_key_canary_cleanup");
    lifecycleSchedules.LastDelete.AuthenticatedOwner!
        .Owner.OwnerSubject.Should().Be("nyx-owner-alpha");
    lifecycleSchedules.LastDelete.AuthenticatedOwner
        .VerifiedBindingId.Should().Be("binding-alpha");
    lifecycleSchedules.LastDelete.ProvisioningBearerToken.Should().Be(
        "fresh-owner-bearer");
}
```

- [ ] **Step 2: Add failing branch-validation tests**

Add:

```csharp
[Theory]
[InlineData("delete-operation-alpha", null)]
[InlineData(null, "delete-idempotency-alpha")]
[InlineData("   ", "delete-idempotency-alpha")]
[InlineData("delete-operation-alpha", "   ")]
public async Task Delete_WithPartialLifecycleIdentity_ShouldRejectBeforeDispatch(
    string? operationId,
    string? idempotencyKey)
{
    var genericSchedules =
        new RecordingScheduledDispatchApplicationService();
    var lifecycleSchedules =
        new RecordingStudioMemberWorkflowSchedulePort();
    var requestHttp = CreateLifecycleDeleteHttpContext(
        lifecycleSchedules,
        new FakeExternalIdentityBindingQueryPort());

    var result = await ScheduledDispatchEndpoints.Delete(
        requestHttp,
        "sch-alpha",
        null,
        new ScheduledDispatchDeleteHttpRequest
        {
            Reason = "cleanup",
            OperationId = operationId,
            IdempotencyKey = idempotencyKey,
            Owner = StudioMemberAutomationOwnerRequest(),
        },
        genericSchedules);

    var responseHttp = CreateHttpContext();
    await result.ExecuteAsync(responseHttp);
    responseHttp.Response.StatusCode.Should().Be(
        StatusCodes.Status400BadRequest);
    genericSchedules.Deleted.Should().BeEmpty();
    genericSchedules.TeamDeleted.Should().BeEmpty();
    lifecycleSchedules.LastDelete.Should().BeNull();
}

[Fact]
public async Task Delete_WithLifecycleIdentityButNoOwner_ShouldRejectBeforeDispatch()
{
    var schedules = new RecordingScheduledDispatchApplicationService();
    var result = await ScheduledDispatchEndpoints.Delete(
        CreateLifecycleDeleteHttpContext(null, null),
        "sch-alpha",
        null,
        new ScheduledDispatchDeleteHttpRequest
        {
            Reason = "cleanup",
            OperationId = "delete-operation-alpha",
            IdempotencyKey = "delete-idempotency-alpha",
        },
        schedules);

    var http = CreateHttpContext();
    await result.ExecuteAsync(http);
    http.Response.StatusCode.Should().Be(
        StatusCodes.Status400BadRequest);
    schedules.Deleted.Should().BeEmpty();
    schedules.TeamDeleted.Should().BeEmpty();
}

[Fact]
public async Task Delete_WithLifecycleIdentityAndMissingStudioCapability_ShouldReturnUnavailable()
{
    var schedules = new RecordingScheduledDispatchApplicationService();
    var result = await ScheduledDispatchEndpoints.Delete(
        CreateLifecycleDeleteHttpContext(null, null),
        "sch-alpha",
        null,
        new ScheduledDispatchDeleteHttpRequest
        {
            Reason = "cleanup",
            OperationId = "delete-operation-alpha",
            IdempotencyKey = "delete-idempotency-alpha",
            Owner = StudioMemberAutomationOwnerRequest(),
        },
        schedules);

    var http = CreateHttpContext();
    await result.ExecuteAsync(http);
    http.Response.StatusCode.Should().Be(
        StatusCodes.Status503ServiceUnavailable);
}
```

Keep or update the existing owner-only delete test so it omits lifecycle
identity and proves that no Studio services are required.

- [ ] **Step 3: Add failing sanitized error tests**

Cover:

```text
missing subject or binding -> 401 TEAM_AUTOMATION_UNAUTHORIZED
malformed bearer -> 401 TEAM_AUTOMATION_UNAUTHORIZED
exact owner/schedule missing -> 404 TEAM_AUTOMATION_NOT_FOUND
reason/operation payload conflict -> 409 TEAM_AUTOMATION_CONFLICT
missing optional Studio capability -> 503 TEAM_AUTOMATION_LIFECYCLE_UNAVAILABLE
```

For every error body, serialize the result and assert it excludes bearer
values, binding IDs, hidden owner values, API-key IDs, Vault references, and
backend exception text.

- [ ] **Step 4: Run endpoint tests and confirm RED**

Run:

```bash
dotnet test \
  test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj \
  --nologo \
  --filter ScheduledDispatchEndpointsTests
```

Expected RED: `ScheduledDispatchDeleteHttpRequest` and the Studio lifecycle
branch do not exist.

- [ ] **Step 5: Add the delete-only HTTP DTO**

Keep `ScheduledDispatchStateChangeHttpRequest` unchanged for enable/disable.
Add these imports to the endpoint file:

```csharp
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.DependencyInjection;
```

Add:

```csharp
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScheduledDispatchDeleteHttpRequest
{
    public string? Reason { get; init; }
    public string? OperationId { get; init; }
    public string? IdempotencyKey { get; init; }
    public ScheduledDispatchOwnerHttpRequest? Owner { get; init; }
}
```

- [ ] **Step 6: Declare both admission receipts and lifecycle errors**

Map the route as:

```csharp
group.MapDelete("/schedules/{scheduleId}", Delete)
    .WithTags("Schedules")
    .Produces<ScheduledDispatchMutationReceipt>(
        StatusCodes.Status202Accepted)
    .Produces<StudioMemberAutomationMutationReceipt>(
        StatusCodes.Status202Accepted)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status403Forbidden)
    .Produces(StatusCodes.Status404NotFound)
    .Produces(StatusCodes.Status409Conflict)
    .Produces(StatusCodes.Status503ServiceUnavailable);
```

- [ ] **Step 7: Implement the three valid delete branches**

Use this handler signature:

```csharp
internal static async Task<IResult> Delete(
    HttpContext http,
    string scheduleId,
    [FromQuery] string? reason,
    [FromBody] ScheduledDispatchDeleteHttpRequest? input,
    [FromServices] IScheduledDispatchApplicationService schedules,
    CancellationToken ct = default)
```

Implement this order:

```csharp
TeamMemberAutomationOwner? owner;
try
{
    owner = input?.Owner?.ToTeamMemberAutomationOwner();
}
catch (ArgumentException ex)
{
    return InvalidTeamAutomationRequest(ex.Message);
}

if (TryCreateOwnerScopeAccessDeniedResult(http, owner, out var denied))
    return denied;

var operationId = NormalizeOptional(input?.OperationId);
var idempotencyKey = NormalizeOptional(input?.IdempotencyKey);
if ((operationId == null) != (idempotencyKey == null))
{
    return InvalidTeamAutomationRequest(
        "operationId and idempotencyKey must be supplied together.");
}

var deleteReason = reason ?? input?.Reason ?? string.Empty;
if (operationId == null)
{
    try
    {
        var receipt = owner == null
            ? await schedules.DeleteAsync(
                scheduleId,
                deleteReason,
                ct)
            : await schedules.DeleteTeamAutomationAsync(
                scheduleId,
                owner,
                deleteReason,
                ct);
        return Results.Accepted(
            BuildScheduleLocation(receipt.ScheduleId, owner),
            receipt);
    }
    catch (Exception ex) when (
        owner != null &&
        TryMapTeamAutomationDeleteError(ex, out var ownerError))
    {
        return ownerError;
    }
    catch (Exception ex) when (
        owner == null &&
        TryMapScheduleMutationError(ex, out var genericError))
    {
        return genericError;
    }
}

if (owner == null)
{
    return InvalidTeamAutomationRequest(
        "owner is required when operationId and idempotencyKey are supplied.");
}

var lifecycleSchedules =
    http.RequestServices.GetService<IStudioMemberWorkflowSchedulePort>();
var bindingQuery =
    http.RequestServices.GetService<IExternalIdentityBindingQueryPort>();
if (lifecycleSchedules == null || bindingQuery == null)
    return TeamAutomationLifecycleUnavailable();

try
{
    var authority =
        await StudioMemberAutomationHttpAuthorityResolver.ResolveAsync(
            http,
            bindingQuery,
            ct);
    var receipt = await lifecycleSchedules.DeleteAsync(
        new StudioMemberAutomationActionCommand(
            owner.ScopeId,
            owner.TeamId,
            owner.MemberId,
            scheduleId,
            operationId,
            idempotencyKey)
        {
            Reason = deleteReason,
            AuthenticatedOwner = authority.AuthenticatedOwner,
            ProvisioningBearerToken =
                authority.ProvisioningBearerToken,
        },
        ct);
    return Results.Accepted(
        BuildScheduleLocation(receipt.ScheduleId, owner),
        receipt);
}
catch (Exception ex) when (
    TryMapTeamAutomationDeleteError(ex, out var lifecycleError))
{
    return lifecycleError;
}
```

Add:

```csharp
private static string? NormalizeOptional(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value.Trim();
```

Use `GetService`, not `GetRequiredService`, because non-Studio hosts retain
generic schedule deletion.

- [ ] **Step 8: Add fixed, non-sensitive lifecycle error mappings**

Use:

```csharp
private static IResult InvalidTeamAutomationRequest(string message) =>
    Results.BadRequest(new
    {
        code = "INVALID_TEAM_AUTOMATION_REQUEST",
        message,
    });

private static IResult TeamAutomationLifecycleUnavailable() =>
    Results.Json(
        new
        {
            code = "TEAM_AUTOMATION_LIFECYCLE_UNAVAILABLE",
            message =
                "Team automation lifecycle capability is unavailable.",
        },
        statusCode: StatusCodes.Status503ServiceUnavailable);

private static IResult TeamAutomationNotFound() =>
    Results.Json(
        new
        {
            code = "TEAM_AUTOMATION_NOT_FOUND",
            message = "Team automation resource was not found.",
        },
        statusCode: StatusCodes.Status404NotFound);

private static bool TryMapTeamAutomationDeleteError(
    Exception exception,
    out IResult result)
{
    result = exception switch
    {
        UnauthorizedAccessException => Results.Json(
            new
            {
                code = "TEAM_AUTOMATION_UNAUTHORIZED",
                message =
                    "Authenticated Team automation authority is required.",
            },
            statusCode: StatusCodes.Status401Unauthorized),
        StudioMemberAutomationNotFoundException =>
            TeamAutomationNotFound(),
        StudioMemberNotFoundException => TeamAutomationNotFound(),
        ScheduledDispatchNotFoundException => TeamAutomationNotFound(),
        ScheduledDispatchConflictException => Results.Json(
            new
            {
                code = "TEAM_AUTOMATION_CONFLICT",
                message =
                    "The Team automation delete conflicts with its active operation.",
            },
            statusCode: StatusCodes.Status409Conflict),
        InvalidOperationException => InvalidTeamAutomationRequest(
            "Team automation delete request is invalid."),
        ArgumentException => InvalidTeamAutomationRequest(
            "Team automation delete request is invalid."),
        _ => null!,
    };
    return result != null;
}
```

- [ ] **Step 9: Verify all endpoint paths GREEN**

Run the Task 4 endpoint command again. Expected:

```text
generic delete: existing receipt and behavior
owner-only delete: existing simple owner path
owner + stable lifecycle identities: Studio rich delete
partial identity or lifecycle-without-owner: 400 before dispatch
missing optional Studio composition: 503
scope mismatch: existing 403 before Application dispatch
owner mismatch: fixed 404
payload conflict: fixed 409
```

- [ ] **Step 10: Commit the canonical Host contract**

```bash
git add \
  src/platform/Aevatar.GAgentService.Hosting/Endpoints/Schedules/ScheduledDispatchEndpoints.cs \
  test/Aevatar.GAgentService.Integration.Tests/ScheduledDispatchEndpointsTests.cs
git commit -m "Add credential-aware schedule delete"
```

---

### Task 5: Update Canon, Run Gates, Review, and Push

**Files:**

- Modify: `docs/canon/scheduled-skill-runners.md`

**Interfaces:**

- Consumes: completed runtime implementation.
- Produces: documented exact replay contract and a verified branch ready for automatic deployment.

- [ ] **Step 1: Replace the stale retry-revocation canon**

Document this request:

```http
DELETE /api/schedules/{scheduleId}
Content-Type: application/json

{
  "reason": "scheduled_agent_key_canary_cleanup",
  "operationId": "delete-operation-...",
  "idempotencyKey": "delete-idempotency-...",
  "owner": {
    "kind": "studio_member_automation",
    "scopeId": "scope-...",
    "teamId": "team-...",
    "memberId": "m-..."
  }
}
```

State explicitly:

```text
The exact same normalized owner, operationId, idempotencyKey, and reason are
replayed while revocation is pending. The Host derives a fresh authenticated
bearer on each request. There is no nested delete or public retry-revocation
route. A 202 receipt is admission only; callers reread canonical owner detail
until both revocation tracks are terminal and the row becomes not found.
```

Remove the canon sentence that tells ordinary callers to use an
identity-only `retry-revocation` action.

- [ ] **Step 2: Run focused tests**

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj \
  --nologo \
  --filter ScheduledDispatchGAgentTests

dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj \
  --nologo \
  --filter "StudioMemberWorkflowSchedulePortTests|StudioMemberAutomationEndpointsTests"

dotnet test \
  test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj \
  --nologo \
  --filter ScheduledDispatchEndpointsTests
```

Expected: zero failed tests in all three commands.

- [ ] **Step 3: Run build and mandatory guards**

```bash
dotnet build aevatar.slnx --nologo
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
```

Expected: build exit 0; every guard and docs lint pass.

- [ ] **Step 4: Review the complete branch**

Generate one review package from base `5bf1a7477` through branch `HEAD`, then
dispatch a broad reviewer. Require it to check:

```text
Actor authority and Protobuf state
reason replay identity and conflict behavior
fresh Host-derived bearer on every replay
no secret fields in body/receipt/error
generic and owner-only compatibility
no nested CRUD/action route or retry route
no query-time replay/priming
test and canon coverage
```

Fix every Critical or Important finding, rerun the covering tests, regenerate
the package, and re-review until clean.

- [ ] **Step 5: Commit documentation**

```bash
git add docs/canon/scheduled-skill-runners.md
git commit -m "Document canonical schedule delete replay"
```

- [ ] **Step 6: Rebase only by replaying commits onto the still-current remote**

Before push:

```bash
git fetch origin feature/integrate
git rev-parse origin/feature/integrate
```

Expected before direct push: the remote is still
`5bf1a74773e3b880d0030ffee6f2bef0fa7d0825`. If it advanced, create a fresh
temporary branch from the new remote and cherry-pick this plan's commits in
order, then rerun the focused tests, build, and guards. Do not force-push.

- [ ] **Step 7: Push to the deployment branch**

```bash
git push origin HEAD:feature/integrate
```

Expected: non-force fast-forward succeeds and triggers the configured
automatic deployment.

## Execution Handoff

After this runtime plan is green and deployed, continue the already-approved
companion plan:

```text
docs/superpowers/plans/2026-07-27-scheduled-agent-key-canary-skill.md
```

First replace its stale nested schedule routes and retry-revocation steps with
the canonical owner-aware list/detail/DELETE contract. Then forward-test,
package, push the Ornn source branch, validate/upload privately, require a
completed green audit, execute one real cron canary through `/v1/responses`,
complete terminal cleanup, and only then change the exact Ornn GUID to public.
