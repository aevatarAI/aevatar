# WorkOrder Product Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep WorkOrder as a first-class, indefinitely durable user-intent resource while removing duplicate approval, Run-payload, and artifact authorities from its contract.

**Architecture:** `WorkOrderGAgent` owns only durable intent, validated assignment snapshots, dispatch coordination, its independent lifecycle, and typed references to the accepted Run and observed Run outcome. Team/member read models remain the assignment authority, Workflow/Run actors remain approval and execution authorities, and ContentArtifact remains content/revision authority. A WorkOrder may omit a deadline, so it can exist before a Run and survive until an explicit lifecycle transition.

**Tech Stack:** .NET 10, C#, Protobuf, actor-owned event sourcing, CQRS projection, xUnit, FluentAssertions.

## Global Constraints

- Preserve one actor-owned WorkOrder lifecycle and the existing `Command -> committed event -> current-state read model` path.
- A WorkOrder owns user intent, requester identity, assignment coordination, dispatch identity, cancellation, reassignment, timeout when supplied, and its own terminal lifecycle.
- Team/member read models own membership, exact `publishedServiceId`, revision, implementation kind, and callability.
- Workflow/Run actors own approval plans, approvers, decisions, output, error, and execution facts.
- ContentArtifact owns result content, revisions, provenance, citations, retention, and redaction.
- WorkOrder stores only input/declared-output references supplied as intent plus a minimal validated Run outcome reference; it never promotes a declared output reference into an actual result.
- A missing `timeoutAtUtc` means no WorkOrder deadline. A supplied deadline must still be later than the request time.
- Persisted state, commands, events, continuations, and projection documents remain Protobuf.
- Remove old approval and terminal-payload surfaces completely; do not add compatibility endpoints, aliases, or deprecated messages.
- Source, tests, documentation, commit messages, PR text, and GitHub comments are English.
- Interface and Protobuf changes remain unapproved until two independent reviewers explicitly approve them; green tests are not review evidence.

---

### Task 1: Freeze The Independent Product And Authority Boundaries In Tests

**Files:**
- Create: `test/Aevatar.Studio.Tests/WorkOrders/WorkOrderAuthorityBoundaryTests.cs`
- Modify: `test/Aevatar.Studio.Tests/WorkOrders/WorkOrderCommandServiceTests.cs`

**Interfaces:**
- Verifies: WorkOrder can be created without a deadline, reassigned, recovered, and cancelled before any Run exists.
- Verifies: public WorkOrder service and DTO contracts expose no approval decision authority.
- Verifies: the target Protobuf contract contains `WorkOrderRunOutcomeReference` without Run payload or artifact fields.

- [x] **Step 1: Add the actor lifecycle test with a missing deadline**

Create a focused test fixture with the same `InMemoryEventStore`, `DefaultEventSourcingBehaviorFactory<WorkOrderState>`, reflected `GAgentBase.SetId`, no-op publisher, and callback scheduler pattern already used by `WorkOrderGAgentTests`. Add this exact behavior:

```csharp
[Fact]
public async Task WorkOrderWithoutDeadline_ShouldSurviveReassignmentRecoveryAndCancellationBeforeAnyRun()
{
    var store = new InMemoryEventStore();
    var created = await CreateAgentAsync(store);

    await created.HandleCreateAsync(BuildCreate(timeoutAtUtc: null));
    created.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Ready);
    created.State.TimeoutAtUtc.Should().BeNull();
    created.State.Run.Should().BeNull();

    await created.HandleReassignAsync(BuildReassign(created.State.LifecycleVersion));

    var recovered = await CreateAgentAsync(store);
    recovered.State.MemberId.Should().Be("member-2");
    recovered.State.Run.Should().BeNull();
    await recovered.HandleCancelAsync(BuildCancel(recovered.State.LifecycleVersion));

    var terminal = await CreateAgentAsync(store);
    terminal.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Cancelled);
    terminal.State.Run.Should().BeNull();
}
```

- [x] **Step 2: Add authority-surface tests**

Add reflection and descriptor assertions:

```csharp
[Fact]
public void PublicContract_ShouldNotExposeApprovalAuthority()
{
    typeof(IWorkOrderService).GetMethods().Select(static method => method.Name)
        .Should().NotContain(["ApproveAsync", "DenyAsync"]);
    typeof(CreateWorkOrderRequest).GetProperties().Select(static property => property.Name)
        .Should().NotContain("PermissionPlan");
    typeof(WorkOrderCurrentStateResponse).GetProperties().Select(static property => property.Name)
        .Should().NotContain(["PermissionPlan", "Approval"]);
    typeof(WorkOrderGAgent).GetMethods().Select(static method => method.Name)
        .Should().NotContain(["HandleApproveAsync", "HandleDenyAsync"]);
}

[Fact]
public void RunOutcomeReference_ShouldContainOnlyValidatedCoordinationFacts()
{
    var descriptor = WorkOrderState.Descriptor.File.MessageTypes
        .SingleOrDefault(static message => message.Name == "WorkOrderRunOutcomeReference");

    descriptor.Should().NotBeNull();
    descriptor!.Fields.InDeclarationOrder().Select(static field => field.Name)
        .Should().BeEquivalentTo(
            "delivery_id",
            "run_id",
            "run_actor_id",
            "command_id",
            "correlation_id",
            "outcome",
            "terminal_at_utc");
}
```

- [x] **Step 3: Change the command-service deadline test to require accepted dispatch**

Replace `CreateAsync_WhenDeadlineMissing_ShouldRejectBeforeActorDispatch` with:

```csharp
[Fact]
public async Task CreateAsync_WhenDeadlineMissing_ShouldDispatchWithoutInventingOne()
{
    var bootstrap = new RecordingBootstrap();
    var dispatchPort = new RecordingDispatchPort();
    var service = new ActorDispatchWorkOrderCommandService(
        bootstrap,
        CreateCommandDispatch(dispatchPort));

    await service.CreateAsync(
        ScopeId,
        CreateRequest() with { TimeoutAtUtc = null },
        new WorkOrderPrincipalContract("requester-1", "user"),
        CreateAssignment());

    bootstrap.ActorIds.Should().ContainSingle();
    var command = dispatchPort.Envelopes.Should().ContainSingle().Subject.Payload!
        .Unpack<CreateWorkOrder>();
    command.TimeoutAtUtc.Should().BeNull();
}
```

- [x] **Step 4: Run the focused tests and verify RED**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~WorkOrderAuthorityBoundaryTests|FullyQualifiedName~WorkOrderCommandServiceTests'
```

Expected: compilation fails because `WorkOrderState.Run` is not generated yet, and the current deadline/approval/descriptor behavior also contradicts the new assertions.

---

### Task 2: Narrow The Actor-Owned Protobuf Lifecycle

**Files:**
- Modify: `agents/Aevatar.GAgents.WorkOrder/work_order_messages.proto`
- Modify: `agents/Aevatar.GAgents.WorkOrder/WorkOrderGAgent.cs`
- Modify: `agents/Aevatar.GAgents.WorkOrder/WorkOrderGAgent.State.cs`
- Modify: `agents/Aevatar.GAgents.WorkOrder/WorkOrderConventions.cs`
- Modify: `test/Aevatar.Studio.Tests/WorkOrders/WorkOrderGAgentTests.cs`
- Modify: `test/Aevatar.GAgentService.Tests/Core/ServiceRunWorkOrderIntegrationTests.cs`

**Interfaces:**
- Produces: `WorkOrderRunLink` as the accepted Run identity snapshot.
- Produces: `WorkOrderRunOutcomeReference` as the minimal terminal observation.
- Removes: permission plans, approver identities, approve/deny commands, and copied terminal payload/artifact fields.
- Keeps: deterministic WorkOrder/dispatch/Run/delivery identities and exact publisher/identity validation.

- [x] **Step 1: Replace duplicate authorities in Protobuf**

Delete `WorkOrderApprovalStatus`, `WorkOrderExternalActionReference`, `WorkOrderPermissionRequirement`, `WorkOrderPermissionPlan`, `WorkOrderApprovalState`, `ApproveWorkOrder`, `DenyWorkOrder`, and `WorkOrderApprovalDecidedEvent`. Remove `WAITING_APPROVAL` and `DENIED` lifecycle values.

Replace execution and terminal messages with:

```proto
message WorkOrderRunLink {
  string run_id = 1;
  string run_actor_id = 2;
  string command_id = 3;
  string correlation_id = 4;
  string revision_id = 5;
  string deployment_id = 6;
  google.protobuf.Timestamp accepted_at_utc = 7;
}

message WorkOrderRunOutcomeReference {
  string delivery_id = 1;
  string run_id = 2;
  string run_actor_id = 3;
  string command_id = 4;
  string correlation_id = 5;
  WorkOrderTerminalOutcome outcome = 6;
  google.protobuf.Timestamp terminal_at_utc = 7;
}
```

In `WorkOrderState`, reserve deleted field numbers `14` and `15`, rename field `24` to `run`, field `25` to `run_outcome`, and field `26` to `late_run_outcome`. In `CreateWorkOrder`, reserve deleted fields `14` and `15`. Replace `WorkOrderPlannedEvent` with a timestamp-only `WorkOrderReadyEvent`, and replace terminal-evidence events with Run-outcome-observed events.

- [x] **Step 2: Make creation always enter the independent ready state**

`HandleCreateAsync` persists `WorkOrderCreatedEvent` followed by `WorkOrderReadyEvent`. It does not inspect permissions or create approval state. `ValidateCreate` permits a missing `timeout_at_utc`; when present, it still verifies the deadline is later than `requested_at_utc`.

- [x] **Step 3: Keep terminal observations reference-only**

Map workflow and ServiceRun notifications into `WorkOrderRunOutcomeReference` using only the seven fields in the contract. Preserve exact delivery/Run/actor/command/correlation checks, envelope publisher validation, duplicate idempotency, conflict rejection, and late-outcome handling after WorkOrder timeout. Do not read notification `Output`, `Error`, or declared result artifacts.

- [x] **Step 4: Support dispatch retry without a WorkOrder deadline**

In `ScheduleExecutionRetryAsync`, compute exponential backoff as today. If a deadline exists, cap the delay at the remaining duration and trigger timeout when elapsed. If no deadline exists, schedule the normal capped backoff without manufacturing an expiry. Keep `deadline_at_utc` absent in the execution request.

- [x] **Step 5: Update existing actor and cross-actor tests**

Remove approval-owner tests and assertions. Replace them with assertions that create reaches `Ready` at lifecycle version `2`, reassignment/cancellation remain available before dispatch, and Run terminal notifications populate `RunOutcome` without storing payload. Rename `Execution` assertions to `Run`, and `TerminalEvidence`/`LateTerminalEvidence` assertions to `RunOutcome`/`LateRunOutcome`.

- [x] **Step 6: Run actor and integration tests and verify GREEN**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkOrder
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo \
  --filter FullyQualifiedName~ServiceRunWorkOrderIntegrationTests
```

Expected: both commands pass. Existing unrelated analyzer warnings may remain, but there are no test failures or new WorkOrder warnings.

---

### Task 3: Remove Approval And Run-Payload Surfaces From Application, API, And Projection

**Files:**
- Modify: `src/Aevatar.Studio.Application/Studio/Contracts/WorkOrderContracts.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Abstractions/IWorkOrderCommandPort.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Abstractions/IWorkOrderService.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/WorkOrderService.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/ValidatedWorkOrderExecutionPort.cs`
- Modify: `src/Aevatar.Studio.Projection/CommandServices/ActorDispatchWorkOrderCommandService.cs`
- Modify: `src/Aevatar.Studio.Projection/ReadModels/studio_projection_readmodels.proto`
- Modify: `src/Aevatar.Studio.Projection/Projectors/WorkOrderCurrentStateProjector.cs`
- Modify: `src/Aevatar.Studio.Projection/QueryPorts/ProjectionWorkOrderQueryPort.cs`
- Modify: `src/Aevatar.Studio.Hosting/Endpoints/WorkOrderEndpoints.cs`
- Modify: `test/Aevatar.Studio.Tests/WorkOrders/WorkOrderAssignmentAndExecutionTests.cs`
- Modify: `test/Aevatar.Studio.Tests/WorkOrders/WorkOrderEndpointsTests.cs`
- Modify: `test/Aevatar.Studio.Tests/WorkOrders/WorkOrderProjectionTests.cs`

**Interfaces:**
- Removes: `ApproveAsync`, `DenyAsync`, `DecideWorkOrderApprovalRequest`, permission-plan DTOs, approval DTOs, and `:approve`/`:deny` routes.
- Produces: `WorkOrderRunLinkResponse` and `WorkOrderRunOutcomeReferenceResponse` with reference-only fields.
- Keeps: create/list/get/reassign/dispatch/cancel APIs and query filters over WorkOrder-owned fields.

- [x] **Step 1: Shrink application contracts and ports**

Remove approval/permission records and members. Replace execution/terminal response records with:

```csharp
public sealed record WorkOrderRunLinkResponse(
    string RunId,
    string RunActorId,
    string CommandId,
    string CorrelationId,
    string RevisionId,
    string DeploymentId,
    DateTimeOffset AcceptedAtUtc);

public sealed record WorkOrderRunOutcomeReferenceResponse(
    string DeliveryId,
    string RunId,
    string RunActorId,
    string CommandId,
    string CorrelationId,
    string Outcome,
    DateTimeOffset TerminalAtUtc);
```

`WorkOrderCurrentStateResponse` exposes `Run`, `RunOutcome`, and `LateRunOutcome`. It no longer exposes `PermissionPlan` or `Approval`.

- [x] **Step 2: Remove approval mutation paths completely**

Delete approve/deny methods from service and command ports, their command mapping and canonicalization cases, and the `:approve`/`:deny` endpoint mappings and handlers. Keep requester authorization for reassign/dispatch/cancel unchanged.

- [x] **Step 3: Project only WorkOrder-owned facts and references**

Delete permission and approval projection messages/fields. Replace `WorkOrderTerminalEvidenceDocument` with `WorkOrderRunOutcomeReferenceDocument` containing the seven reference fields. Map `state.Run`, `state.RunOutcome`, and `state.LateRunOutcome`; do not materialize Run output, error, or result artifact lists.

- [x] **Step 4: Represent no deadline without an artificial timeout**

The command service leaves `CreateWorkOrder.TimeoutAtUtc` unset when the request omits it. `ValidatedWorkOrderExecutionPort` maps an absent deadline to `long.MaxValue` only at the existing completion-notification transport boundary, whose contract requires a positive expiration; a supplied deadline remains the exact expiration.

- [x] **Step 5: Update service, endpoint, projection, and execution tests**

Delete approval endpoint fakes/assertions, update DTO constructors, and verify that projected Run outcomes contain identities/outcome/time only. Keep different fixtures for `memberId`, `workflowId`, and `publishedServiceId`.

- [x] **Step 6: Run Studio tests and required test guard**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkOrder
bash tools/ci/test_stability_guards.sh
```

Expected: `81` baseline WorkOrder tests adjusted by deliberate removals/additions all pass, and the stability guard exits `0`.

---

### Task 4: Update Canonical Architecture And Verify The Branch

**Files:**
- Modify: `docs/canon/work-orders.md`
- Modify: `docs/superpowers/plans/2026-07-23-work-order-product-lifecycle.md`

**Interfaces:**
- Documents: the product requirement that makes WorkOrder irreducible to dispatch/Run.
- Documents: exact authority ownership and query composition boundaries.
- Provides: fresh local verification evidence for the PR.

- [x] **Step 1: Rewrite the canonical boundary and lifecycle sections**

Document these exact invariants:

- WorkOrder exists before a Run and may have no deadline.
- It can be reassigned or cancelled before dispatch independently of a Run.
- Its committed history remains after terminal completion.
- It is not a schedule and does not own recurring trigger/credential automation.
- It does not approve connector actions; Workflow/Run owns suspended approval continuation.
- It stores no Run output/error and no claimed result artifacts; consumers resolve Run and ContentArtifact authorities through typed references.
- Its Run outcome enum is a validated observation used only to advance WorkOrder's own lifecycle.

Remove the permission/approval sections and `:approve`/`:deny` endpoint documentation.

- [x] **Step 2: Run targeted format, diff, and architecture checks**

```bash
dotnet format test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --no-restore
git diff --check
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
```

Expected: every command exits `0`.

- [x] **Step 3: Run build and full solution tests**

```bash
dotnet build aevatar.slnx --nologo --no-restore
dotnet test aevatar.slnx --nologo --no-build
```

Expected: build has `0` errors and the full solution has `0` failed tests. Existing warnings are reported separately and are not represented as new failures.

Fresh verification on 2026-07-23 after rebasing onto `origin/feature/integrate`:

- `dotnet build aevatar.slnx --nologo --no-restore` exited `0` with `0` errors and `226` existing warnings.
- `dotnet test aevatar.slnx --nologo --no-build` ran the full solution. Every test project passed except five positive-path cases in `AgentProfileRolloutProvisioningTests`, which returned `1` because the copied `Grpc.Tools` `protoc` is a macOS x86_64 executable on an Apple Silicon host (`Bad CPU type in executable`). The WorkOrder branch does not change the failing test, rollout tool, or project file.
- With `PROTOC=/opt/homebrew/bin/protoc`, `AgentProfileRolloutProvisioningTests` passed `22/23`; only the case that deliberately clears `PROTOC` and `PATH` to force the incompatible bundled executable remained red. This is recorded as a host-specific baseline limitation, not represented as a WorkOrder failure or a green full-solution run. Linux PR CI remains the authority for the full-solution result.
- WorkOrder-focused verification passed `84/84` Studio tests and `1/1` ServiceRun integration test. The no-deadline queue-full retry test was verified red against the former deadline-required guard (`expected 1, found 0`) and green after restoring the deadline-independent retry path.
- `test_stability_guards.sh`, `architecture_guards.sh` (`15/15` architecture tests), docs lint, and `git diff --check` exited `0`.

- [x] **Step 4: Commit the coherent contract change**

```bash
git add agents/Aevatar.GAgents.WorkOrder \
  src/Aevatar.Studio.Application \
  src/Aevatar.Studio.Hosting \
  src/Aevatar.Studio.Projection \
  test/Aevatar.Studio.Tests/WorkOrders \
  test/Aevatar.GAgentService.Tests/Core/ServiceRunWorkOrderIntegrationTests.cs \
  docs/canon/work-orders.md \
  docs/superpowers/plans/2026-07-23-work-order-product-lifecycle.md
git commit -m "Narrow WorkOrder to durable intent coordination"
```

- [ ] **Step 5: Push and open the review PR**

```bash
git push -u origin feat/2026-07-23_work-order-product-lifecycle
gh pr create --repo aevatarAI/aevatar \
  --base feature/integrate \
  --head feat/2026-07-23_work-order-product-lifecycle \
  --title "Narrow WorkOrder to durable intent coordination" \
  --body-file /tmp/work-order-product-lifecycle-pr.md
```

The PR body states the product requirement, authority removals, affected paths, every verification command/result, #2789, and Discussion #2878. It requests two independent interface approvals and does not claim merge readiness.

- [ ] **Step 6: Update the Discussion and issue with evidence**

Post an English Discussion reply explaining how the implementation keeps first-class WorkOrder while addressing the duplicate-authority concern. Post a concise #2789 progress comment linking the PR and verification. Do not close #2789 and do not merge into `feature/integrate` until two independent approvals are recorded.
