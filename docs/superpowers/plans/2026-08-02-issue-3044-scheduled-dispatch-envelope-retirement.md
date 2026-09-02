# Issue 3044 Scheduled Dispatch Envelope Retirement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent external callers and legacy public schedules from dispatching caller-supplied envelopes to arbitrary actors while preserving typed service invocation and an explicitly marked internal actor protocol.

**Architecture:** Hosting exposes only catalog-resolved service invocation targets and checks target tenant authority. Application admits and returns only service-invocation schedules. Core stores a typed Protobuf envelope authority so legacy unmarked schedules fail closed while trusted internal actor commands remain explicit.

**Tech Stack:** .NET 10, C#, ASP.NET Core minimal APIs, Google Protobuf, xUnit, FluentAssertions

## Global Constraints

- Keep `scopeId`, `memberId`, `workflowId`, `publishedServiceId`, and `actorId` distinct.
- Public HTTP and Application contracts must not expose a raw-envelope scheduling capability.
- Stable actor control semantics must be represented by Protobuf fields, not metadata bags.
- Existing Workflow service invocation and Studio member automation behavior must remain functional.
- Every production behavior change starts with a test that fails for the expected reason.

---

### Task 1: Retire the HTTP Envelope Contract and Enforce Target Scope

**Files:**
- Modify: `test/Aevatar.GAgentService.Integration.Tests/ScheduledDispatchEndpointsTests.cs`
- Modify: `src/platform/Aevatar.GAgentService.Hosting/Endpoints/Schedules/ScheduledDispatchEndpoints.cs`

**Interfaces:**
- Consumes: `AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(HttpContext, string, out IResult)`
- Produces: `ScheduledDispatchConfigurationHttpRequest` with only `ServiceInvocation` as a target

- [ ] **Step 1: Write failing HTTP contract tests**

Add an actual host request containing distinct identities and a workflow control payload:

```csharp
var response = await host.Client.PostAsJsonAsync("/api/schedules", new
{
    scheduleId = "schedule-alpha",
    cronExpression = "0 9 * * *",
    envelope = new
    {
        actorId = "actor-cross-owner",
        envelope = new
        {
            payload = new
            {
                typeUrl = "type.googleapis.com/aevatar.workflow.WorkflowStoppedEvent",
                value = Convert.ToBase64String(Array.Empty<byte>()),
            },
        },
    },
});

response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
host.Schedules.Created.Should().BeEmpty();
```

Add direct create/update tests proving `scope-alpha` cannot target a service in
`scope-beta`, while a `scope-alpha` target still reaches the recording service.

- [ ] **Step 2: Run the tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --no-restore --filter "FullyQualifiedName~ScheduledDispatchEndpointsTests"
```

Expected: the raw envelope request is accepted and the cross-scope service request reaches the application service.

- [ ] **Step 3: Remove the raw HTTP target and add scope admission**

Delete `ScheduledDispatchEnvelopeTargetHttpRequest` and the `Envelope` property.
Resolve exactly one service invocation target:

```csharp
if (ServiceInvocation == null)
    throw new ArgumentException("A service invocation scheduled dispatch target is required.");

return await ServiceInvocation.ToResolvedTargetAsync(
    catalogReader,
    revisionCatalogReader,
    authenticatedOwnerSubject,
    ct);
```

After `ToConfigurationAsync`, enforce authority before mutation:

```csharp
var targetScopeId = configuration.Target.ServiceInvocation?.Identity.TenantId;
if (TryCreateOwnerScopeAccessDeniedResult(http, targetScopeId, out denied))
    return denied;
```

Apply this to both create and update and convert unrelated endpoint test helpers
from envelope targets to typed service invocation targets.

- [ ] **Step 4: Run the focused endpoint tests and verify GREEN**

Run the command from Step 2. Expected: all `ScheduledDispatchEndpointsTests` pass.

### Task 2: Close the Application Capability and Hide Legacy Rows

**Files:**
- Modify: `test/Aevatar.GAgentService.Tests/Application/ScheduledDispatchApplicationServiceTests.cs`
- Modify: `test/Aevatar.GAgentService.Tests/Application/ScheduledDispatchServiceInvocationTests.cs`
- Modify: `src/platform/Aevatar.GAgentService.Application/Schedules/ScheduledDispatchApplicationService.cs`
- Modify: `src/platform/Aevatar.GAgentService.Application/Schedules/ScheduledDispatchTargetPreparationService.cs`

**Interfaces:**
- Consumes: `IScheduledDispatchQueryPort`
- Produces: public Application behavior restricted to `ScheduledDispatchTargetKind.ServiceInvocation`

- [ ] **Step 1: Write failing Application admission and visibility tests**

Add tests that submit an envelope configuration and assert no actor dispatch:

```csharp
var act = () => service.CreateAsync(CreateRawEnvelopeConfiguration("schedule-alpha"));

await act.Should().ThrowAsync<ArgumentException>()
    .WithMessage("*raw envelope*not supported*");
actorPort.Created.Should().BeEmpty();
```

Use one deliberately named raw fixture for the rejection cases:

```csharp
private static ScheduledDispatchConfiguration CreateRawEnvelopeConfiguration(string scheduleId) =>
    new(
        scheduleId,
        string.Empty,
        new ScheduledDispatchTargetDescriptor(
            ScheduledDispatchTargetKind.Envelope,
            ActorId: "actor-cross-owner",
            Envelope: new EventEnvelope { Payload = Any.Pack(new Empty()) }),
        "0 9 * * *",
        "UTC",
        true,
        new Dictionary<string, string>());
```

Seed a query result with `TargetKind = Envelope` and prove `GetAsync`, list,
enable, disable, delete, and run-now do not return or mutate it. Add a target
preparation test proving direct envelope preparation is rejected.

- [ ] **Step 2: Run the tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --no-restore --filter "FullyQualifiedName~ScheduledDispatchApplicationServiceTests|FullyQualifiedName~ScheduledDispatchServiceInvocationTests"
```

Expected: envelope creation/preparation and legacy lifecycle tests fail because the current Application still accepts them.

- [ ] **Step 3: Implement service-invocation-only Application behavior**

Make target normalization reject envelopes:

```csharp
return target.Kind switch
{
    ScheduledDispatchTargetKind.ServiceInvocation => NormalizeServiceInvocationTarget(target),
    ScheduledDispatchTargetKind.Envelope => throw new ArgumentException(
        "Raw envelope scheduled dispatch targets are not supported by the Application contract.",
        nameof(target)),
    _ => throw new ArgumentException(
        $"Unsupported scheduled dispatch target kind '{target.Kind}'.",
        nameof(target)),
};
```

Force `TargetKind = ServiceInvocation` on list queries, require service invocation
for get/team get, and make `GetMutableScheduleAsync` treat every other target as
not found. Make target preparation throw for envelope targets instead of building
an arbitrary actor envelope. Convert general-purpose test fixtures to service
invocation targets; retain one explicit raw fixture only for rejection tests.

- [ ] **Step 4: Run the focused Application tests and verify GREEN**

Run the command from Step 2. Expected: all selected tests pass.

### Task 3: Fence the Internal Actor Protocol and Retire Unmarked State

**Files:**
- Modify: `src/platform/Aevatar.GAgentService.Core/Schedules/scheduled_dispatch_state.proto`
- Modify: `src/platform/Aevatar.GAgentService.Core/Schedules/ScheduledDispatchGAgent.cs`
- Modify: `test/Aevatar.Workflow.Core.Tests/ScheduledDispatchGAgentTests.cs`

**Interfaces:**
- Produces: `ScheduledDispatchEnvelopeAuthorityState.TrustedInternal`
- Consumes: `ScheduledDispatchTargetState.EnvelopeAuthority`

- [ ] **Step 1: Write failing actor authority tests**

Add a configuration test with an unmarked envelope target:

```csharp
var command = CreateConfigureCommand(target: new ScheduledDispatchTargetState
{
    Kind = ScheduledDispatchTargetKindState.Envelope,
    ActorId = "actor-cross-owner",
    Envelope = CreateTriggerEnvelope("actor-cross-owner", new Empty()),
});

var act = () => agent.HandleConfigureAsync(command);
await act.Should().ThrowAsync<ArgumentException>()
    .WithMessage("*trusted internal authority*");
```

Create a snapshot containing an enabled legacy envelope target with unspecified
authority. On activation assert `Enabled == false`, callback purge occurred, and
no dispatch occurred. A manual fire must throw the stable retirement error and
leave both dispatch ports empty. Keep an explicit `TrustedInternal` fire test.

- [ ] **Step 2: Run the tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~ScheduledDispatchGAgentTests"
```

Expected: unmarked configuration and legacy activation/manual-fire tests fail because authority is not yet represented or checked.

- [ ] **Step 3: Add the Protobuf authority and fail-closed actor logic**

Add:

```protobuf
enum ScheduledDispatchEnvelopeAuthorityState
{
  SCHEDULED_DISPATCH_ENVELOPE_AUTHORITY_STATE_UNSPECIFIED = 0;
  SCHEDULED_DISPATCH_ENVELOPE_AUTHORITY_STATE_TRUSTED_INTERNAL = 1;
}

message ScheduledDispatchTargetState
{
  // existing fields 1-6
  ScheduledDispatchEnvelopeAuthorityState envelope_authority = 7;
}
```

Preserve the field in envelope target normalization. Command validation accepts
an envelope only when the authority is `TrustedInternal`. Activation calls a
single helper that persists a disabled event for unmarked legacy envelope state,
purges callbacks, and returns before scheduling. Fire calls the same predicate;
manual fire throws and automatic fire disables/purges without dispatch.

Update the common actor test helper to mark its intentional internal envelope
targets as `TrustedInternal` so unrelated low-level runtime tests preserve their
meaning.

- [ ] **Step 4: Run the focused actor tests and verify GREEN**

Run the command from Step 2. Expected: all `ScheduledDispatchGAgentTests` pass.

### Task 4: Document the Public Boundary and Verify the Issue

**Files:**
- Modify: `docs/canon/scheduled-skill-runners.md`
- Modify: `docs/superpowers/specs/2026-08-02-issue-3044-scheduled-dispatch-envelope-retirement-design.md`

**Interfaces:**
- Produces: canonical documented service-invocation-only public schedule contract

- [ ] **Step 1: Update canonical documentation**

State explicitly that `/api/schedules` accepts only catalog-resolved
`serviceInvocation`, enforces authenticated scope versus target tenant, and
does not expose actor IDs or raw envelopes. Document that legacy unmarked
envelope schedules are hidden and disabled, while the Protobuf
`TrustedInternal` marker is an actor-only contract.

- [ ] **Step 2: Run affected project suites**

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --no-restore
dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --no-restore
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --no-restore
```

Expected: zero failed tests.

- [ ] **Step 3: Run repository guards and full verification**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
dotnet build aevatar.slnx --nologo --no-restore
dotnet test aevatar.slnx --nologo --no-build --no-restore
git diff --check
```

Expected: every command exits zero and the full test summary reports zero failures.

- [ ] **Step 4: Commit the completed issue implementation**

Stage only files listed in this plan and commit with:

```bash
git commit -m "Block external raw envelope schedules"
```

- [ ] **Step 5: Request independent review**

Give a reviewer issue `#3044`, the design document, the branch diff against
`origin/feature/integrate`, and all verification evidence. Resolve every Critical
or Important finding with another RED/GREEN cycle, then repeat verification.
