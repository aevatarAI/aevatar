# WorkOrder Continuation And Terminal Outbox Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the WorkOrder actor-turn blocking and terminal-delivery loss modes so the `feature/integrate -> dev` candidate is ready for production review.

**Architecture:** `WorkOrderGAgent` commits dispatch intent, performs only a non-blocking handoff, and consumes typed accepted/failed continuations. A bounded hosted worker performs read-model validation and service dispatch outside actor turns. Role, script, and ServiceRun actors own durable deadline-bounded terminal outboxes with typed retry state.

**Tech Stack:** .NET 10, C#, Protobuf, xUnit, FluentAssertions, `System.Threading.Channels`, `BackgroundService`, actor durable callbacks.

## Global Constraints

- `WorkOrderGAgent` remains the only authority for WorkOrder lifecycle state.
- Actor turns never wait for read-model access or cross-actor service dispatch.
- Background queues are transport only; no process-local ID-to-state registries.
- Every execution continuation carries `work_order_id`, `dispatch_command_id`, and `requested_run_id`.
- All persisted state, events, retry signals, and continuations use Protobuf.
- WorkOrder-originated retries expire at the WorkOrder deadline.
- Pending terminal deliveries cannot be overwritten or trimmed.
- Preserve current NyxIdChat progress streaming, typed tool presentation, and
  workflow tool failure behavior merged in Task 0.
- Retry delay starts at `250 ms` and is capped at `30 seconds` and the remaining deadline.
- Queue defaults are capacity `1024`, max concurrency `64`, shutdown drain grace `10` seconds.
- Keep `apps/aevatar-console-web` byte-for-byte identical to `origin/dev`.
- Run `bash tools/ci/test_stability_guards.sh` after every test change.
- Design source: `docs/superpowers/specs/2026-07-21-work-order-continuation-terminal-outbox-fixes-design.md`.
- Do not update remote `feature/integrate` without explicit `--force-with-lease` authorization.

---

### Task 0: Align The Candidate With Current Remote State

**Files:**
- Merge: latest `origin/feature/integrate` backend changes.
- Merge: latest `origin/dev` changes.
- Preserve: local Studio query-tool fixes and WorkOrder review-fix design/plan.
- Replace from dev: `apps/aevatar-console-web/**`.

**Interfaces:**
- Produces: a clean candidate containing both current remote tips before Task 1 starts.
- Verifies: all six previously fixed Studio query blockers remain fixed.

- [ ] **Step 1: Refresh and record remote tips**

```bash
git fetch origin --prune
git rev-parse HEAD origin/dev origin/feature/integrate
git status --short --branch
```

Expected: worktree clean. Record all three SHAs in the task report.

- [ ] **Step 2: Merge the current feature tip**

```bash
git merge --no-edit origin/feature/integrate
```

Resolve backend conflicts by preserving both the remote behavior and the local
query-tool fixes. In particular, retain `IStudioMemberAutomationQueryPort`,
`WorkflowCatalogAgentToolSource`, nested Team schedule URLs, stable
`authorization_status`, and the GAgent guard meta-test.

- [ ] **Step 3: Merge dev and take the complete dev frontend tree**

```bash
git merge --no-commit --no-ff origin/dev
git restore --source=origin/dev --staged --worktree apps/aevatar-console-web
git add -A
git commit -m "Merge latest dev into integration candidate"
```

If the merge reports frontend conflicts, the `git restore` command is the
complete resolution for every frontend path. Resolve backend conflicts by
semantic contract, never by taking an entire side blindly.

- [ ] **Step 4: Verify retained behavior and frontend identity**

```bash
dotnet test test/Aevatar.AI.ToolProviders.StudioProvisioning.Tests/Aevatar.AI.ToolProviders.StudioProvisioning.Tests.csproj --nologo
dotnet test test/Aevatar.AI.ToolProviders.Workflow.Tests/Aevatar.AI.ToolProviders.Workflow.Tests.csproj --nologo
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo
git diff --quiet origin/dev -- apps/aevatar-console-web
git diff --check
git status --short --branch
```

Expected: all suites pass, frontend equality exits `0`, diff check is clean,
and the worktree has no uncommitted source changes.

---

### Task 1: Make WorkOrder Execution A Typed Actor Continuation

**Files:**
- Create: `agents/Aevatar.GAgents.WorkOrder/IWorkOrderExecutionScheduler.cs`
- Modify: `agents/Aevatar.GAgents.WorkOrder/work_order_messages.proto`
- Modify: `agents/Aevatar.GAgents.WorkOrder/WorkOrderGAgent.cs`
- Modify: `agents/Aevatar.GAgents.WorkOrder/WorkOrderGAgent.State.cs`
- Modify: `test/Aevatar.Studio.Tests/WorkOrders/WorkOrderGAgentTests.cs`
- Modify: `test/Aevatar.GAgentService.Tests/Core/ServiceRunWorkOrderIntegrationTests.cs`

**Interfaces:**
- Produces: `IWorkOrderExecutionScheduler.ScheduleAsync(WorkOrderExecutionRequest, CancellationToken)`.
- Produces: accepted/failed continuation messages and durable retry messages.
- Keeps: `IWorkOrderExecutionPort` for the off-actor worker in Task 2.

- [ ] **Step 1: Replace execution-port tests with scheduler/continuation tests**

Add these exact behaviors to `WorkOrderGAgentTests.cs`:

```csharp
[Fact]
public async Task ExecuteWorkOrder_ShouldOnlyScheduleAndRemainDispatchPending()
{
    var scheduler = new RecordingExecutionScheduler();
    var agent = await CreateDispatchPendingAgentAsync(scheduler);

    await agent.HandleExecuteAsync(new ExecuteWorkOrder
    {
        WorkOrderId = agent.State.WorkOrderId,
        DispatchCommandId = agent.State.DispatchCommandId,
    });

    scheduler.Requests.Should().ContainSingle();
    agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
    agent.State.Execution.RunId.Should().BeEmpty();
}

[Fact]
public async Task AcceptedContinuation_AfterTimeout_ShouldBeIgnored()
{
    var agent = await CreateTimedOutDispatchAgentAsync();
    var version = agent.State.LifecycleVersion;

    await agent.HandleExecutionAcceptedAsync(BuildAcceptedContinuation(agent.State));

    agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.TimedOut);
    agent.State.LifecycleVersion.Should().Be(version);
}

[Fact]
public async Task ExecutionRetryFired_WhenAttemptIsStale_ShouldNotSchedule()
{
    var scheduler = new RecordingExecutionScheduler();
    var agent = await CreateDispatchPendingAgentAsync(scheduler);

    await agent.HandleExecutionRetryFiredAsync(new WorkOrderExecutionRetryFired
    {
        WorkOrderId = agent.State.WorkOrderId,
        DispatchCommandId = agent.State.DispatchCommandId,
        RequestedRunId = agent.State.RequestedRunId,
        Attempt = 99,
    });

    scheduler.Requests.Should().BeEmpty();
}
```

Also add:

- `CreateWorkOrder_WithoutDeadline_ShouldRejectBeforePersisting`
- `CreateWorkOrder_WhenDeadlineIsNotAfterRequestedAt_ShouldRejectBeforePersisting`
- `ExecuteWorkOrder_WhenQueueFull_ShouldScheduleDurableRetryWithoutFailing`
- `ExecutionWatchdog_WhenStillPending_ShouldReenqueueCanonicalRequest`
- `ActivateAsync_WhenDispatchPending_ShouldReenqueueAndRestoreWatchdog`
- `FailedContinuation_WhenCorrelationMismatches_ShouldBeIgnored`

Implement `CreateDispatchPendingAgentAsync` in the test by calling the existing
`CreateAgentAsync` helper with a recording scheduler, then the existing create
and dispatch commands. `CreateTimedOutDispatchAgentAsync` uses the same helper,
fires `WorkOrderTimeoutFired`, and asserts `TimedOut` before returning.

Update the integration router so `ExecuteWorkOrder` records a scheduled request,
then the test explicitly routes `WorkOrderExecutionAcceptedContinuation` back
to `HandleExecutionAcceptedAsync` before terminal evidence is delivered.

- [ ] **Step 2: Run the actor suites and verify RED**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkOrderGAgentTests
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo \
  --filter FullyQualifiedName~ServiceRunWorkOrderIntegrationTests
```

Expected: compile failures because the scheduler and continuation contracts do not exist.

- [ ] **Step 3: Add the exact Protobuf control contracts**

Append these state fields and messages to `work_order_messages.proto` using the
shown field numbers:

```proto
message WorkOrderState {
  // Existing fields 1-29 remain unchanged.
  int32 execution_retry_attempt = 30;
  string execution_retry_callback_id = 31;
  google.protobuf.Timestamp execution_retry_at_utc = 32;
}

message WorkOrderExecutionRequest {
  // Existing fields 1-14 remain unchanged.
  google.protobuf.Timestamp deadline_at_utc = 15;
}

message WorkOrderExecutionAcceptedContinuation {
  string work_order_id = 1;
  string dispatch_command_id = 2;
  string requested_run_id = 3;
  WorkOrderExecutionAccepted accepted = 4;
}

message WorkOrderExecutionFailedContinuation {
  string work_order_id = 1;
  string dispatch_command_id = 2;
  string requested_run_id = 3;
  WorkOrderExecutionFailed failed = 4;
}

message WorkOrderExecutionRetryScheduledEvent {
  string work_order_id = 1;
  string dispatch_command_id = 2;
  string requested_run_id = 3;
  int32 attempt = 4;
  string callback_id = 5;
  google.protobuf.Timestamp retry_at_utc = 6;
}

message WorkOrderExecutionRetryFired {
  string work_order_id = 1;
  string dispatch_command_id = 2;
  string requested_run_id = 3;
  int32 attempt = 4;
}
```

Create the scheduler port:

```csharp
namespace Aevatar.GAgents.WorkOrder;

public interface IWorkOrderExecutionScheduler
{
    ValueTask ScheduleAsync(
        WorkOrderExecutionRequest request,
        CancellationToken ct = default);
}

public sealed class WorkOrderExecutionQueueFullException(string message) : Exception(message);
```

- [ ] **Step 4: Replace synchronous execution with scheduling and correlation checks**

Change the actor constructor to accept `IWorkOrderExecutionScheduler?`. The
`HandleExecuteAsync` handler must only clone/schedule the request and arrange a
durable watchdog. Add two handlers with this exact guard shape:

```csharp
private bool MatchesPendingExecution(string workOrderId, string dispatchCommandId, string requestedRunId) =>
    State.LifecycleStatus == WorkOrderLifecycleStatus.DispatchPending &&
    string.Equals(State.WorkOrderId, workOrderId, StringComparison.Ordinal) &&
    string.Equals(State.DispatchCommandId, dispatchCommandId, StringComparison.Ordinal) &&
    string.Equals(State.RequestedRunId, requestedRunId, StringComparison.Ordinal);

[EventHandler(EndpointName = "workOrderExecutionAccepted")]
public async Task HandleExecutionAcceptedAsync(WorkOrderExecutionAcceptedContinuation continuation)
{
    if (!MatchesPendingExecution(
            continuation.WorkOrderId,
            continuation.DispatchCommandId,
            continuation.RequestedRunId))
        return;

    ValidateAcceptedExecution(continuation.Accepted);
    await PersistDomainEventAsync(new WorkOrderRunAcceptedEvent
    {
        Accepted = continuation.Accepted.Clone(),
    });
}

[EventHandler(EndpointName = "workOrderExecutionFailed")]
public async Task HandleExecutionFailedAsync(WorkOrderExecutionFailedContinuation continuation)
{
    if (!MatchesPendingExecution(
            continuation.WorkOrderId,
            continuation.DispatchCommandId,
            continuation.RequestedRunId))
        return;

    await PersistDomainEventAsync(new WorkOrderDispatchFailedEvent
    {
        Failure = continuation.Failed?.Failure?.Clone() ?? new WorkOrderFailureReference
        {
            Code = "WORK_ORDER_DISPATCH_FAILED",
            Message = "WorkOrder execution failed without a typed failure.",
            Source = "work-order-execution-worker",
            ReferenceId = continuation.DispatchCommandId,
        },
        FailedAtUtc = continuation.Failed?.FailedAtUtc?.Clone()
            ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    });
}
```

Use private constants `250` and `30_000` for exponential retry. Persist
`WorkOrderExecutionRetryScheduledEvent` only after durable callback scheduling
succeeds. State reducers must ignore mismatched or non-increasing attempts and
clear retry fields on accepted, failed, timeout, cancel, deny, or terminal
completion. `ValidateCreate` must require `TimeoutAtUtc` and require it to be
later than `RequestedAtUtc`; update all WorkOrder test create fixtures with a
future deadline.

- [ ] **Step 5: Run focused suites and guards GREEN**

Run Step 2 again, then:

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
git diff --check
```

Expected: both focused suites pass and all commands exit `0`.

- [ ] **Step 6: Commit**

```bash
git add agents/Aevatar.GAgents.WorkOrder \
  test/Aevatar.Studio.Tests/WorkOrders/WorkOrderGAgentTests.cs \
  test/Aevatar.GAgentService.Tests/Core/ServiceRunWorkOrderIntegrationTests.cs
git commit -m "Continue WorkOrder execution off actor turns"
```

---

### Task 2: Add The Off-Actor WorkOrder Execution Worker

**Files:**
- Create: `src/Aevatar.Studio.Application/Studio/Services/WorkOrderExecutionQueue.cs`
- Create: `src/Aevatar.Studio.Application/Studio/Services/WorkOrderExecutionScheduler.cs`
- Create: `src/Aevatar.Studio.Application/Studio/Services/WorkOrderExecutionService.cs`
- Create: `src/Aevatar.Studio.Application/Studio/Services/WorkOrderExecutionWorkerOptions.cs`
- Create: `src/Aevatar.Studio.Hosting/WorkOrders/WorkOrderExecutionWorker.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/ValidatedWorkOrderExecutionPort.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/Aevatar.Studio.Application/Aevatar.Studio.Application.csproj`
- Modify: `src/Aevatar.Studio.Hosting/StudioHostingServiceCollectionExtensions.cs`
- Modify: `src/Aevatar.Studio.Hosting/Aevatar.Studio.Hosting.csproj`
- Modify: `src/Aevatar.Studio.Projection/CommandServices/ActorDispatchWorkOrderCommandService.cs`
- Test: `test/Aevatar.Studio.Tests/WorkOrders/WorkOrderAssignmentAndExecutionTests.cs`
- Test: `test/Aevatar.Studio.Tests/WorkOrders/WorkOrderCommandServiceTests.cs`
- Test: `test/Aevatar.Studio.Tests/StudioApplicationServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: Task 1 scheduler and continuation Protobuf contracts.
- Produces: bounded queue, off-actor execution service, hosted worker, deadline propagation.

- [ ] **Step 1: Write failing queue, worker, DI, and deadline tests**

Add exact tests for:

- `WorkOrderExecutionQueue_WhenFull_ShouldThrowWithoutBlocking`
- `WorkOrderExecutionService_WhenAccepted_ShouldDispatchAcceptedContinuation`
- `WorkOrderExecutionService_WhenUnexpectedFailure_ShouldDispatchSafeFailedContinuation`
- `WorkOrderExecutionService_WhenContinuationDispatchFails_ShouldSurfaceForWatchdogRecovery`
- `ValidatedExecution_ShouldUseWorkOrderDeadlineForBothCompletionTargets`
- `CreateAsync_WhenDeadlineMissing_ShouldRejectBeforeActorDispatch`
- `CreateAsync_WhenDeadlineElapsed_ShouldRejectBeforeActorDispatch`
- `AddStudioApplication_ShouldAliasWorkOrderSchedulerAndRegisterQueueSingleton`
- `AddStudioHostingCore_ShouldRegisterWorkOrderExecutionWorker`
- `BlockedWorker_ShouldNotBlockWorkOrderTimeout`

The deadline assertion must be:

```csharp
invocation.WorkflowCompletionNotificationTarget.ExpiresAtUnixMs.Should().Be(deadline.ToUnixTimeMilliseconds());
invocation.ServiceRunCompletionNotificationTarget.ExpiresAtUnixMs.Should().Be(deadline.ToUnixTimeMilliseconds());
```

The safe failure assertion must prove the continuation contains
`WORK_ORDER_EXECUTION_UNEXPECTED_FAILURE` and the exception type name, but not
the exception message.

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo \
  --filter "FullyQualifiedName~WorkOrderAssignmentAndExecutionTests|FullyQualifiedName~StudioApplicationServiceCollectionExtensionsTests"
```

Expected: compile failures for the queue, execution service, worker options, and worker.

- [ ] **Step 3: Implement the bounded transport**

Use these exact option defaults and queue behavior:

```csharp
public sealed class WorkOrderExecutionWorkerOptions
{
    public const string SectionName = "Aevatar:Studio:WorkOrderExecutionWorker";
    public int QueueCapacity { get; set; } = 1024;
    public int MaxConcurrency { get; set; } = 64;
    public int ShutdownDrainGraceSeconds { get; set; } = 10;
    public TimeSpan ShutdownDrainGrace => ShutdownDrainGraceSeconds > 0
        ? TimeSpan.FromSeconds(ShutdownDrainGraceSeconds)
        : TimeSpan.Zero;
}

public interface IWorkOrderExecutionQueue
{
    void Enqueue(WorkOrderExecutionRequest request);
    IAsyncEnumerable<WorkOrderExecutionRequest> DequeueAllAsync(CancellationToken ct = default);
}
```

`Enqueue` must clone the Protobuf request and use `TryWrite`. It throws
`WorkOrderExecutionQueueFullException` when full and never calls `WriteAsync`.

- [ ] **Step 4: Implement worker execution and continuation dispatch**

`WorkOrderExecutionService.ExecuteAsync` calls `IWorkOrderExecutionPort`, maps
the oneof result to the exact Task 1 continuation, and dispatches an envelope:

```csharp
private static EventEnvelope BuildEnvelope(
    string targetActorId,
    string operationId,
    IMessage continuation) =>
    new()
    {
        Id = operationId,
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        Payload = Any.Pack(continuation),
        Route = EnvelopeRouteSemantics.CreateDirect(
            "studio.work-order-execution-worker",
            targetActorId),
        Propagation = new EnvelopePropagation
        {
            CorrelationId = operationId,
        },
    };
```

Use operation ID
`work-order-execution-result:{workOrderId}:{dispatchCommandId}`. The hosted
worker drains with a `SemaphoreSlim(MaxConcurrency)`, executes with
`CancellationToken.None`, logs last-resort faults, and waits up to the shutdown
grace for in-flight permits.

- [ ] **Step 5: Propagate the WorkOrder deadline**

`BuildExecutionRequest` must clone `State.TimeoutAtUtc` into
`DeadlineAtUtc`. `ValidatedWorkOrderExecutionPort` must reject a missing or
non-positive deadline for `run_origin = work-order` and set both completion
target expiries from it. Remove both `long.MaxValue` assignments.
`ActorDispatchWorkOrderCommandService.CreateAsync` must reject a missing
`TimeoutAtUtc` or one not later than the request time before actor bootstrap or
dispatch.

- [ ] **Step 6: Register services and verify GREEN**

Register singleton queue, scheduler, and execution service in
`AddStudioApplication`. Bind options and add the hosted worker in
`AddStudioHostingCore`. Add direct project references from Studio.Application
to `Aevatar.Foundation.Abstractions` and from Studio.Hosting to
`Aevatar.Studio.Application`; both projects use these dependencies directly.

Run Step 2 again, then:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
git diff --check
```

Expected: Studio suite and guards pass.

- [ ] **Step 7: Commit**

```bash
git add src/Aevatar.Studio.Application src/Aevatar.Studio.Hosting \
  test/Aevatar.Studio.Tests
git commit -m "Run WorkOrder execution outside actor turns"
```

---

### Task 3: Make ServiceRun Terminal Delivery Retry In Healthy Processes

**Files:**
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/Protos/service_runs.proto`
- Modify: `src/platform/Aevatar.GAgentService.Core/GAgents/ServiceRunGAgent.cs`
- Test: `test/Aevatar.GAgentService.Tests/Core/ServiceRunGAgentTests.cs`
- Test: `test/Aevatar.GAgentService.Tests/Core/ServiceRunWorkOrderIntegrationTests.cs`

**Interfaces:**
- Consumes: existing `ServiceRunCompletionNotificationTarget.ExpiresAtUnixMs`.
- Produces: actor-owned retry state and durable self-events.

- [ ] **Step 1: Add failing delivery retry tests**

Add exact tests:

- `TerminalSendFailure_ShouldScheduleDurableRetryWithoutDeactivation`
- `RetryFired_WhenPending_ShouldDispatchAndCommitDispatched`
- `RetryFired_WhenAttemptIsStale_ShouldNotSend`
- `TerminalDelivery_WhenDeadlineElapsed_ShouldCommitExpired`
- `ActivateAsync_WhenRetryScheduled_ShouldRecoverPendingNotification`

The first test must use a throwing publisher plus a recording
`IRuntimeCallbackScheduler`, then assert status `RetryScheduled`, attempt `1`,
and one durable callback payload.

- [ ] **Step 2: Run tests and verify RED**

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo \
  --filter "FullyQualifiedName~ServiceRunGAgentTests|FullyQualifiedName~ServiceRunWorkOrderIntegrationTests"
```

Expected: failures because send errors escape and no retry state exists.

- [ ] **Step 3: Add Protobuf retry state**

```proto
enum ServiceRunTerminalNotificationDeliveryStatus {
  SERVICE_RUN_TERMINAL_NOTIFICATION_DELIVERY_STATUS_UNSPECIFIED = 0;
  SERVICE_RUN_TERMINAL_NOTIFICATION_DELIVERY_STATUS_PREPARED = 1;
  SERVICE_RUN_TERMINAL_NOTIFICATION_DELIVERY_STATUS_DISPATCHED = 2;
  SERVICE_RUN_TERMINAL_NOTIFICATION_DELIVERY_STATUS_EXPIRED = 3;
  SERVICE_RUN_TERMINAL_NOTIFICATION_DELIVERY_STATUS_RETRY_SCHEDULED = 4;
}

message ServiceRunState {
  // Existing fields 1-5 remain unchanged.
  int32 terminal_notification_attempt = 6;
  string terminal_notification_retry_callback_id = 7;
  google.protobuf.Timestamp terminal_notification_retry_at = 8;
}

message ServiceRunTerminalNotificationRetryScheduledEvent {
  string delivery_id = 1;
  int32 attempt = 2;
  string callback_id = 3;
  google.protobuf.Timestamp retry_at = 4;
}

message ServiceRunTerminalNotificationRetryFiredEvent {
  string delivery_id = 1;
  int32 attempt = 2;
}
```

- [ ] **Step 4: Implement retry and expiry**

Catch non-cancellation send failures, calculate exponential delay using
`250 ms`/`30 seconds`, schedule a durable self-event, then commit the scheduled
event. The retry handler accepts either `RetryScheduled` with the same attempt
or `Prepared` with `attempt == state.Attempt + 1`. Dispatched/expired reducers
clear the pending notification and retry callback fields.

- [ ] **Step 5: Verify GREEN and commit**

Run Step 2 again, then:

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/projection_state_version_guard.sh
git diff --check
git add src/platform/Aevatar.GAgentService.Abstractions/Protos/service_runs.proto \
  src/platform/Aevatar.GAgentService.Core/GAgents/ServiceRunGAgent.cs \
  test/Aevatar.GAgentService.Tests/Core
git commit -m "Retry ServiceRun terminal delivery"
```

---

### Task 4: Protect Role Completion Deliveries From Trimming

**Files:**
- Modify: `src/Aevatar.AI.Abstractions/ai_messages.proto`
- Modify: `src/Aevatar.AI.Core/RoleGAgent.cs`
- Modify: `src/platform/Aevatar.GAgentService.Infrastructure/Dispatch/DefaultServiceInvocationDispatcher.cs`
- Create: `test/Aevatar.AI.Tests/RoleGAgentCompletionNotificationTests.cs`
- Modify: `test/Aevatar.AI.Tests/RoleGAgentReplayContractTests.cs`
- Modify: `test/Aevatar.GAgentService.Tests/Infrastructure/DefaultServiceInvocationDispatcherTests.cs`

**Interfaces:**
- Produces: per-session completion delivery status, retry signal, deadline.
- Consumes: WorkOrder service-run completion expiry from Task 2.

- [ ] **Step 1: Write failing Role outbox and propagation tests**

Add exact cases:

- `CompletionSendFailure_ShouldScheduleDurableRetry`
- `PendingCompletion_ShouldNotBeTrimmedWhenSessionLimitExceeded`
- `RetryFired_ShouldDispatchMatchingSessionOnce`
- `RetryFired_WhenSessionOrAttemptIsStale_ShouldNotSend`
- `CompletionDeadlineElapsed_ShouldCommitExpired`
- `ActivateAsync_WhenCompletionPending_ShouldRestoreRetry`
- `StaticDispatch_ShouldForwardInternalDeliveryIdAndWorkOrderExpiry`

Use 129 completed sessions to prove the oldest pending entry remains while the
oldest terminal-delivery entry is pruned.

- [ ] **Step 2: Run tests and verify RED**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo \
  --filter FullyQualifiedName~RoleGAgentCompletionNotificationTests
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo \
  --filter FullyQualifiedName~DefaultServiceInvocationDispatcherTests
```

Expected: missing fields/events and current send/trim behavior fail.

- [ ] **Step 3: Add the Role delivery contracts**

Add `completion_notification_delivery_id = 5` and
`completion_notification_expires_at_unix_ms = 6` to `RoleChatRunContext`.
Reserve `RoleChatSessionState` field `19` and its old name, then add:

```proto
enum RoleChatCompletionNotificationDeliveryStatus {
  ROLE_CHAT_COMPLETION_NOTIFICATION_DELIVERY_STATUS_UNSPECIFIED = 0;
  ROLE_CHAT_COMPLETION_NOTIFICATION_DELIVERY_STATUS_PREPARED = 1;
  ROLE_CHAT_COMPLETION_NOTIFICATION_DELIVERY_STATUS_RETRY_SCHEDULED = 2;
  ROLE_CHAT_COMPLETION_NOTIFICATION_DELIVERY_STATUS_DISPATCHED = 3;
  ROLE_CHAT_COMPLETION_NOTIFICATION_DELIVERY_STATUS_EXPIRED = 4;
}

// RoleChatSessionState fields 1-18 and the committed wire fields below remain unchanged.
int64 last_progress_sequence = 20;
repeated ToolResultEvent tool_results = 21;
RoleChatCompletionNotificationDeliveryStatus completion_notification_delivery_status = 22;
int32 completion_notification_attempt = 23;
string completion_notification_retry_callback_id = 24;
google.protobuf.Timestamp completion_notification_retry_at = 25;
```

Add module-specific retry-scheduled, retry-fired, dispatched, and expired events
carrying `session_id + delivery_id + attempt`.

- [ ] **Step 4: Implement delivery state and safe trimming**

Session completion sets `Prepared` when a target exists. Delivery failure
schedules durable retry. `TrimTrackedSessions` may remove only sessions for
which there is no completion target or status is `Dispatched`/`Expired`; it may
temporarily retain more than 128 sessions when all excess entries are pending.

For static dispatch, derive the internal source delivery ID as
`service-run-source:{runId}:{commandId}` and forward the WorkOrder target expiry.
When no external completion target exists, forward expiry `0` without inventing
a deadline.

- [ ] **Step 5: Verify GREEN and commit**

Run Step 2 again, then:

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
git diff --check
git add docs/superpowers/plans/2026-07-21-work-order-continuation-terminal-outbox-fixes.md \
  src/Aevatar.AI.Abstractions/ai_messages.proto \
  src/Aevatar.AI.Core/RoleGAgent.cs \
  src/platform/Aevatar.GAgentService.Infrastructure/Dispatch/DefaultServiceInvocationDispatcher.cs \
  test/Aevatar.AI.Tests/RoleGAgentCompletionNotificationTests.cs \
  test/Aevatar.AI.Tests/RoleGAgentReplayContractTests.cs \
  test/Aevatar.GAgentService.Tests/Infrastructure/DefaultServiceInvocationDispatcherTests.cs
git commit -m "Retry Role completion delivery"
```

---

### Task 5: Preserve Multiple Script Terminal Deliveries

**Files:**
- Modify: `src/Aevatar.Scripting.Abstractions/script_host_messages.proto`
- Modify: `src/Aevatar.Scripting.Core/Ports/IScriptRuntimeCommandPort.cs`
- Modify: `src/Aevatar.Scripting.Core/ScriptBehaviorGAgent.cs`
- Modify: `src/Aevatar.Scripting.Infrastructure/Ports/ScriptingCommandDispatchModels.cs`
- Modify: `src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptCommandService.cs`
- Modify: `src/Aevatar.Scripting.Infrastructure/Ports/RunScriptRuntimeCommandEnvelopeFactory.cs`
- Modify: `src/platform/Aevatar.GAgentService.Infrastructure/Dispatch/DefaultServiceInvocationDispatcher.cs`
- Modify: `test/Aevatar.Scripting.Core.Tests/Runtime/ScriptBehaviorCompletionNotificationTests.cs`
- Modify: `test/Aevatar.Scripting.Core.Tests/Runtime/RecordingRuntimeCommandPort.cs`
- Modify: `test/Aevatar.Scripting.Core.Tests/Runtime/ScriptAgentLifecycleCapabilitiesTests.cs`
- Modify: `test/Aevatar.GAgentService.Tests/Application/ScriptServiceRunInteractionTests.cs`
- Modify: `test/Aevatar.GAgentService.Tests/Infrastructure/DefaultServiceInvocationDispatcherTests.cs`
- Modify: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowInfrastructureCoverageTests.cs`

**Interfaces:**
- Produces: multi-run script outcome delivery state and durable retry.
- Consumes: the service-run internal delivery target defined in Task 4.

- [ ] **Step 1: Write failing multi-run and retry tests**

Add exact cases:

- `SecondRun_ShouldNotOverwriteFirstPendingOutcome`
- `Replay_ShouldResolveOutcomeByRequestedRunId`
- `SendFailure_ShouldScheduleDurableRetry`
- `RetryFired_ShouldDispatchMatchingOutcome`
- `Pruning_ShouldRemoveOnlyOldestDispatchedOrExpiredOutcome`
- `DeadlineElapsed_ShouldExpirePendingOutcome`
- `ActivateAsync_ShouldAttemptEveryPendingOutcomeInOccurrenceOrder`
- `ScriptingDispatch_ShouldForwardDeliveryIdAndExpiry`

The overwrite test must complete run A with a throwing publisher, complete run B,
and assert both run IDs remain in state with run A still pending.

- [ ] **Step 2: Run tests and verify RED**

```bash
dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo \
  --filter FullyQualifiedName~ScriptBehaviorCompletionNotificationTests
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo \
  --filter "FullyQualifiedName~ScriptServiceRunInteractionTests|FullyQualifiedName~DefaultServiceInvocationDispatcherTests"
```

Expected: current single-outcome state overwrites run A and retry fields are absent.

- [ ] **Step 3: Replace the single slot with typed delivery entries**

Reserve `ScriptBehaviorState` fields `20`, `21` and their names. Add:

```proto
enum ScriptRunOutcomeDeliveryStatus {
  SCRIPT_RUN_OUTCOME_DELIVERY_STATUS_UNSPECIFIED = 0;
  SCRIPT_RUN_OUTCOME_DELIVERY_STATUS_PREPARED = 1;
  SCRIPT_RUN_OUTCOME_DELIVERY_STATUS_RETRY_SCHEDULED = 2;
  SCRIPT_RUN_OUTCOME_DELIVERY_STATUS_DISPATCHED = 3;
  SCRIPT_RUN_OUTCOME_DELIVERY_STATUS_EXPIRED = 4;
}

message ScriptRunOutcomeDeliveryState {
  ScriptRunOutcomeRecordedEvent outcome = 1;
  string delivery_id = 2;
  int64 expires_at_unix_time_ms = 3;
  ScriptRunOutcomeDeliveryStatus status = 4;
  int32 attempt = 5;
  string retry_callback_id = 6;
  int64 retry_at_unix_time_ms = 7;
}

// ScriptBehaviorState keeps fields 1-19.
map<string, ScriptRunOutcomeDeliveryState> run_outcomes = 22;
```

Add delivery ID/expiry to `RunScriptRequestedEvent` and
`ScriptRunOutcomeRecordedEvent`, plus retry-scheduled, retry-fired, dispatched,
and expired events carrying `run_id + delivery_id + attempt`.

- [ ] **Step 4: Propagate the typed script target**

Extend `IScriptRuntimeCommandPort.RunRuntimeAsync` and
`RunScriptRuntimeCommand` with `completionNotificationDeliveryId` and
`completionNotificationExpiresAtUnixMs`. The envelope factory copies them to
Protobuf. The service dispatcher uses
`service-run-source:{runId}:{commandId}` and the external WorkOrder expiry, or
`0` when no explicit expiry exists. Update every listed test implementation of
`IScriptRuntimeCommandPort` to record the two new values.

- [ ] **Step 5: Implement replay, retry, and bounded pruning**

Key state by `ScriptRunId`. Never remove `Prepared` or `RetryScheduled` entries.
After each terminal state transition, prune the oldest `Dispatched`/`Expired`
entries until at most 64 terminal-history entries remain. Activation attempts
all pending entries in occurrence order.

- [ ] **Step 6: Verify GREEN and commit**

Run Step 2 again, then:

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
git diff --check
git add src/Aevatar.Scripting.Abstractions src/Aevatar.Scripting.Core \
  src/Aevatar.Scripting.Infrastructure \
  src/platform/Aevatar.GAgentService.Infrastructure/Dispatch/DefaultServiceInvocationDispatcher.cs \
  test/Aevatar.Scripting.Core.Tests/Runtime/ScriptBehaviorCompletionNotificationTests.cs \
  test/Aevatar.GAgentService.Tests
git commit -m "Preserve Script terminal deliveries"
```

---

### Task 6: Close Boundary And Workflow Tool Review Gaps

**Files:**
- Modify: `src/Aevatar.Studio.Hosting/Endpoints/WorkOrderEndpoints.cs`
- Modify: `test/Aevatar.Studio.Tests/WorkOrders/WorkOrderEndpointsTests.cs`
- Modify: `src/Aevatar.AI.ToolProviders.Workflow/Tools/AevatarWorkflowCatalogTools.cs`
- Modify: `test/Aevatar.AI.ToolProviders.Workflow.Tests/WorkflowCatalogToolsTests.cs`

**Interfaces:**
- Produces: HTTP 400 for malformed WorkOrder identity.
- Produces: fixed workflow tool wire DTOs and complete regression coverage.

- [ ] **Step 1: Add failing boundary and tool contract tests**

Add:

- `HandleGetAsync_WhenWorkOrderIdIsMalformed_ShouldReturnBadRequest`
- `HandleDispatchAsync_WhenWorkOrderIdIsMalformed_ShouldReturnBadRequest`
- `AddWorkflowTools_ShouldResolveWorkflowCatalogSource`
- `WorkflowCatalogTools_ShouldForwardCallerCancellationToken`
- `ListWorkflows_ShouldExposeExactPropertySets`
- `GetWorkflow_ShouldExposeExactNestedPropertySets`

Use `JsonElement.EnumerateObject().Select(x => x.Name)` and exact ordered sets.
The catalog item set is:

```text
name, description, category, group, group_label, sort_order, source,
source_label, show_in_library, is_primitive_example, requires_llm_provider,
primitives, authority_state_version, projection_watermark, last_event_id
```

Detail root is exactly `catalog, yaml, definition, edges`. Definition is
exactly `name, description, closed_world_mode, roles, steps`. Role objects are
exactly `id, name, system_prompt, provider, model, temperature, max_tokens,
max_tool_rounds, max_history_messages, event_modules, event_routes, connectors`.
Step objects are exactly `id, type, target_role, parameters, next, branches,
children`; child objects are exactly `id, type, target_role`; edge objects are
exactly `from, to, label`.

- [ ] **Step 2: Run tests and verify RED**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkOrderEndpointsTests
dotnet test test/Aevatar.AI.ToolProviders.Workflow.Tests/Aevatar.AI.ToolProviders.Workflow.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkflowCatalogToolsTests
```

Expected: malformed identity escapes the endpoint and tool contract assertions fail.

- [ ] **Step 3: Map malformed identities to typed 400**

Catch `ArgumentException` only around application calls that parse canonical
WorkOrder identity in both `HandleGetAsync` and the shared
`HandleRequesterCommandAsync`. Return:

```csharp
BadRequest(
    "INVALID_WORK_ORDER_ID",
    "The WorkOrder identity is malformed.");
```

Do not expose the exception message.

- [ ] **Step 4: Add dedicated workflow wire DTOs**

Map application models into internal records before serialization. Include all
current fields explicitly; do not serialize `WorkflowCatalogItem` or
`WorkflowCatalogItemDetail` directly. Record the caller token in the test port
and assert `ReferenceEquals` is not applicable to structs; compare equality and
`CanBeCanceled`/`IsCancellationRequested`.

Use this explicit wire shape:

```csharp
internal sealed record WorkflowCatalogListJson(
    IReadOnlyList<WorkflowCatalogItemJson> Workflows,
    int Count);

internal sealed record WorkflowCatalogItemJson(
    string Name,
    string Description,
    string Category,
    string Group,
    string GroupLabel,
    int SortOrder,
    string Source,
    string SourceLabel,
    bool ShowInLibrary,
    bool IsPrimitiveExample,
    bool RequiresLlmProvider,
    IReadOnlyList<string> Primitives,
    long AuthorityStateVersion,
    DateTimeOffset ProjectionWatermark,
    string LastEventId);

internal sealed record WorkflowCatalogDetailJson(
    WorkflowCatalogItemJson Catalog,
    string Yaml,
    WorkflowCatalogDefinitionJson Definition,
    IReadOnlyList<WorkflowCatalogEdgeJson> Edges);

internal sealed record WorkflowCatalogDefinitionJson(
    string Name,
    string Description,
    bool ClosedWorldMode,
    IReadOnlyList<WorkflowCatalogRoleJson> Roles,
    IReadOnlyList<WorkflowCatalogStepJson> Steps);

internal sealed record WorkflowCatalogRoleJson(
    string Id,
    string Name,
    string SystemPrompt,
    string Provider,
    string Model,
    float? Temperature,
    int? MaxTokens,
    int? MaxToolRounds,
    int? MaxHistoryMessages,
    IReadOnlyList<string> EventModules,
    string EventRoutes,
    IReadOnlyList<string> Connectors);

internal sealed record WorkflowCatalogStepJson(
    string Id,
    string Type,
    string TargetRole,
    IReadOnlyDictionary<string, string> Parameters,
    string Next,
    IReadOnlyDictionary<string, string> Branches,
    IReadOnlyList<WorkflowCatalogChildStepJson> Children);

internal sealed record WorkflowCatalogChildStepJson(
    string Id,
    string Type,
    string TargetRole);

internal sealed record WorkflowCatalogEdgeJson(
    string From,
    string To,
    string Label);
```

- [ ] **Step 5: Verify GREEN and commit**

Run Step 2 again, then:

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
git diff --check
git add src/Aevatar.Studio.Hosting/Endpoints/WorkOrderEndpoints.cs \
  test/Aevatar.Studio.Tests/WorkOrders/WorkOrderEndpointsTests.cs \
  src/Aevatar.AI.ToolProviders.Workflow/Tools/AevatarWorkflowCatalogTools.cs \
  test/Aevatar.AI.ToolProviders.Workflow.Tests/WorkflowCatalogToolsTests.cs
git commit -m "Harden WorkOrder and workflow query contracts"
```

---

### Task 7: Verify And Re-Review The Complete Candidate

**Files:**
- Verify: all source, Protobuf, tests, guards, and design documents.
- Do not modify: `apps/aevatar-console-web/**`.

**Interfaces:**
- Produces: a reviewed local candidate and updated verification report.
- Does not update: remote `feature/integrate` without explicit authorization.

- [ ] **Step 1: Run affected project suites**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo
dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo
dotnet test test/Aevatar.AI.ToolProviders.Workflow.Tests/Aevatar.AI.ToolProviders.Workflow.Tests.csproj --nologo
```

Expected: zero failures.

- [ ] **Step 2: Run every repository gate required by the design**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/architecture_guards.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/projection_route_mapping_guard.sh
bash tools/ci/solution_split_guards.sh
bash tools/ci/test_solution_ownership_guard.sh
bash tools/ci/slow_test_guards.sh
bash tools/docs/lint.sh
```

Expected: every command exits `0`; report zero-discovery or environment skips.

- [ ] **Step 3: Run full build/test and invariants**

```bash
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo --no-build
git diff --quiet origin/dev -- apps/aevatar-console-web
git diff --check origin/dev..HEAD
git status --short --branch
```

Expected: build/test exit `0`, frontend diff exit `0`, diff check clean, worktree clean.

- [ ] **Step 4: Request a fresh whole-branch review**

Generate a review package for `origin/dev..HEAD`. The reviewer must explicitly
recheck the two former Important findings, all final-review Minor findings,
WorkOrder permission/identity boundaries, and the updated outbox recovery tests.
Fix all Critical/Important findings in one fix wave and re-review.

- [ ] **Step 5: Refresh remote refs and prepare a clean local candidate**

```bash
git fetch origin --prune
git rev-parse origin/dev origin/feature/integrate HEAD
```

If either remote ref moved, integrate it and repeat Steps 1-4. When stable,
create a one-commit candidate from the reviewed tree with parent `origin/dev`,
verify the tree hashes are equal, and verify `origin/dev..clean-head` is exactly
one commit.

- [ ] **Step 6: Stop for explicit force-push authorization**

Do not push. Present the exact guarded command using
`--force-with-lease=refs/heads/feature/integrate:<observed-old-sha>` and wait for
explicit user approval. Only after approval may the controller update the
remote branch and create `feature/integrate -> dev`.
