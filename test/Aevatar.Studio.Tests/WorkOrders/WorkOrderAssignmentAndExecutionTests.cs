using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.WorkOrder;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Hosting.WorkOrders;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Tests.WorkOrders;

public sealed class WorkOrderAssignmentValidatorTests
{
    [Fact]
    public async Task ValidateAsync_ShouldReturnAuthoritativeDistinctIdentities()
    {
        var validator = CreateValidator();

        var result = await validator.ValidateAsync(
            "scope-1",
            "team-1",
            "member-1",
            "service-1",
            "run");

        result.MemberId.Should().Be("member-1");
        result.PublishedServiceId.Should().Be("service-1");
        result.WorkflowId.Should().Be("workflow-1");
        result.ServiceRevisionId.Should().Be("revision-1");
        result.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
    }

    [Theory]
    [InlineData("scope-other", "team-1", "service-1", "revision-1", "WorkOrder Team was not found")]
    [InlineData("scope-1", "team-other", "service-1", "revision-1", "does not belong")]
    [InlineData("scope-1", "team-1", "service-other", "revision-1", "does not match")]
    [InlineData("scope-1", "team-1", "service-1", "revision-stale", "stale revision")]
    public async Task ValidateAsync_ShouldFailClosed_WhenAuthorityRelationshipDoesNotMatch(
        string teamScopeId,
        string memberTeamId,
        string memberServiceId,
        string readinessRevisionId,
        string expectedMessage)
    {
        var validator = CreateValidator(
            teamScopeId: teamScopeId,
            memberTeamId: memberTeamId,
            memberServiceId: memberServiceId,
            readinessRevisionId: readinessRevisionId);

        var act = () => validator.ValidateAsync(
            "scope-1",
            "team-1",
            "member-1",
            "service-1",
            "run");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{expectedMessage}*");
    }

    [Fact]
    public async Task ValidateAsync_ShouldFailClosed_WhenServiceIsNotCallable()
    {
        var validator = CreateValidator(invokeReady: false);

        var act = () => validator.ValidateAsync(
            "scope-1",
            "team-1",
            "member-1",
            "service-1",
            "run");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not callable*");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRequireWorkflowIdentityForWorkflowMember()
    {
        var validator = CreateValidator(workflowId: null);

        var act = () => validator.ValidateAsync(
            "scope-1",
            "team-1",
            "member-1",
            "service-1",
            "run");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no authoritative workflow identity*");
    }

    internal static WorkOrderAssignmentValidator CreateValidator(
        string teamScopeId = "scope-1",
        string memberTeamId = "team-1",
        string memberServiceId = "service-1",
        string bindingRevisionId = "revision-1",
        string readinessRevisionId = "revision-1",
        string? workflowId = "workflow-1",
        string implementationKind = MemberImplementationKindNames.Workflow,
        bool invokeReady = true)
    {
        var now = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        var team = new StudioTeamSummaryResponse(
            "team-1",
            teamScopeId,
            "Team One",
            string.Empty,
            TeamLifecycleStageNames.Active,
            1,
            now,
            now);
        var implementationRef = new StudioMemberImplementationRefResponse(
            implementationKind,
            WorkflowId: workflowId);
        var summary = new StudioMemberSummaryResponse(
            "member-1",
            "scope-1",
            "Member One",
            string.Empty,
            implementationKind,
            MemberLifecycleStageNames.BindReady,
            memberServiceId,
            bindingRevisionId,
            now,
            now)
        {
            TeamId = memberTeamId,
            ImplementationRef = implementationRef,
        };
        var member = new StudioMemberDetailResponse(
            summary,
            implementationRef,
            new StudioMemberBindingContractResponse(
                memberServiceId,
                bindingRevisionId,
                implementationKind,
                now));
        var readiness = new ScopeBindingReadinessSnapshot(
            "scope-1",
            "service-1",
            invokeReady ? ScopeBindingReadinessStatus.Ready : ScopeBindingReadinessStatus.PreparedArtifactMissing,
            ServiceCatalogVisible: true,
            ServingSetVisible: true,
            EligibleServingTargetVisible: invokeReady,
            InvokeReady: invokeReady,
            RevisionId: readinessRevisionId,
            DeploymentId: "deployment-1",
            ObservedAtUtc: now);
        return new WorkOrderAssignmentValidator(
            new FixedTeamQueryPort(team),
            new FixedMemberQueryPort(member),
            new FixedReadinessQueryPort(readiness));
    }

    private sealed class FixedTeamQueryPort(StudioTeamSummaryResponse? team) : IStudioTeamQueryPort
    {
        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId,
            StudioTeamRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, team == null ? [] : [team]));

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default) => Task.FromResult(team);
    }

    private sealed class FixedMemberQueryPort(StudioMemberDetailResponse? member) : IStudioMemberQueryPort
    {
        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(
                scopeId,
                member == null ? [] : [member.Summary]));

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) => Task.FromResult(member);
    }

    private sealed class FixedReadinessQueryPort(ScopeBindingReadinessSnapshot readiness)
        : IScopeBindingReadinessQueryPort
    {
        public Task<ScopeBindingReadinessSnapshot> GetReadinessAsync(
            ScopeBindingReadinessRequest request,
            CancellationToken ct = default) => Task.FromResult(readiness);
    }
}

public sealed class ValidatedWorkOrderExecutionPortTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldRevalidateAndPreserveWorkflowRunIdentityAndCallback()
    {
        var invocationPort = new RecordingInvocationPort();
        var port = new ValidatedWorkOrderExecutionPort(
            WorkOrderAssignmentValidatorTests.CreateValidator(),
            invocationPort);
        var request = BuildExecutionRequest();

        var result = await port.ExecuteAsync(request);

        result.ResultCase.Should().Be(WorkOrderExecutionResult.ResultOneofCase.Accepted);
        result.Accepted.RunId.Should().Be("run-1");
        result.Accepted.CommandId.Should().Be("command-1");
        invocationPort.Requests.Should().ContainSingle();
        var invoked = invocationPort.Requests[0];
        invoked.Identity.TenantId.Should().Be("scope-1");
        invoked.Identity.ServiceId.Should().Be("service-1");
        invoked.CommandId.Should().Be("command-1");
        invoked.CorrelationId.Should().Be("command-1");
        invoked.RequestedRunId.Should().Be("run-1");
        invoked.WorkflowCompletionNotificationTarget.ActorId.Should().Be("work-order:scope-1:wo-1");
        invoked.WorkflowCompletionNotificationTarget.DeliveryId.Should().Be("delivery-1");
        invoked.ServiceRunCompletionNotificationTarget.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldUseServiceRunCallbackForGAgentImplementation()
    {
        var invocationPort = new RecordingInvocationPort();
        var port = new ValidatedWorkOrderExecutionPort(
            WorkOrderAssignmentValidatorTests.CreateValidator(
                workflowId: null,
                implementationKind: MemberImplementationKindNames.GAgent),
            invocationPort);
        var request = BuildExecutionRequest();
        request.WorkflowId = string.Empty;
        request.ImplementationKind = MemberImplementationKindNames.GAgent;

        var result = await port.ExecuteAsync(request);

        result.ResultCase.Should().Be(WorkOrderExecutionResult.ResultOneofCase.Accepted);
        var invoked = invocationPort.Requests.Should().ContainSingle().Subject;
        invoked.WorkflowCompletionNotificationTarget.Should().BeNull();
        invoked.ServiceRunCompletionNotificationTarget.ActorId.Should().Be("work-order:scope-1:wo-1");
        invoked.ServiceRunCompletionNotificationTarget.DeliveryId.Should().Be("delivery-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailBeforeInvocation_WhenAssignmentChangedAfterAuthorization()
    {
        var invocationPort = new RecordingInvocationPort();
        var port = new ValidatedWorkOrderExecutionPort(
            WorkOrderAssignmentValidatorTests.CreateValidator(bindingRevisionId: "revision-2", readinessRevisionId: "revision-2"),
            invocationPort);

        var result = await port.ExecuteAsync(BuildExecutionRequest());

        result.ResultCase.Should().Be(WorkOrderExecutionResult.ResultOneofCase.Failed);
        result.Failed.Failure.Code.Should().Be("WORK_ORDER_ASSIGNMENT_NOT_DISPATCHABLE");
        invocationPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosed_WhenInvocationReceiptChangesRunIdentity()
    {
        var invocationPort = new RecordingInvocationPort
        {
            ReceiptRunId = "different-run",
        };
        var port = new ValidatedWorkOrderExecutionPort(
            WorkOrderAssignmentValidatorTests.CreateValidator(),
            invocationPort);

        var result = await port.ExecuteAsync(BuildExecutionRequest());

        result.ResultCase.Should().Be(WorkOrderExecutionResult.ResultOneofCase.Failed);
        result.Failed.Failure.Code.Should().Be("WORK_ORDER_RUN_IDENTITY_MISMATCH");
    }

    [Theory]
    [InlineData("correlation")]
    [InlineData("targetActor")]
    [InlineData("deployment")]
    public async Task ExecuteAsync_ShouldFailClosed_WhenInvocationReceiptCannotBuildAuthorizedRunLink(
        string invalidField)
    {
        var invocationPort = new RecordingInvocationPort
        {
            ReceiptCorrelationId = invalidField == "correlation" ? "correlation-unrelated" : "command-1",
            ReceiptTargetActorId = invalidField == "targetActor" ? string.Empty : "workflow-run-actor-1",
            ReceiptDeploymentId = invalidField == "deployment" ? string.Empty : "deployment-1",
        };
        var port = new ValidatedWorkOrderExecutionPort(
            WorkOrderAssignmentValidatorTests.CreateValidator(),
            invocationPort);

        var result = await port.ExecuteAsync(BuildExecutionRequest());

        result.ResultCase.Should().Be(WorkOrderExecutionResult.ResultOneofCase.Failed);
        result.Failed.Failure.Code.Should().Be("WORK_ORDER_RUN_IDENTITY_MISMATCH");
    }

    [Fact]
    public async Task ValidatedExecution_ShouldUseWorkOrderDeadlineForBothCompletionTargets()
    {
        var deadline = DateTimeOffset.Parse("2026-07-17T01:00:00Z");
        var workflowInvocationPort = new RecordingInvocationPort();
        var workflowPort = new ValidatedWorkOrderExecutionPort(
            WorkOrderAssignmentValidatorTests.CreateValidator(),
            workflowInvocationPort);
        var workflowRequest = BuildExecutionRequest();
        workflowRequest.DeadlineAtUtc = Timestamp.FromDateTimeOffset(deadline);

        await workflowPort.ExecuteAsync(workflowRequest);

        var invocation = workflowInvocationPort.Requests.Should().ContainSingle().Subject;
        invocation.WorkflowCompletionNotificationTarget.ExpiresAtUnixMs.Should().Be(deadline.ToUnixTimeMilliseconds());

        var serviceInvocationPort = new RecordingInvocationPort();
        var servicePort = new ValidatedWorkOrderExecutionPort(
            WorkOrderAssignmentValidatorTests.CreateValidator(
                workflowId: null,
                implementationKind: MemberImplementationKindNames.GAgent),
            serviceInvocationPort);
        var serviceRequest = BuildExecutionRequest();
        serviceRequest.WorkflowId = string.Empty;
        serviceRequest.ImplementationKind = MemberImplementationKindNames.GAgent;
        serviceRequest.DeadlineAtUtc = Timestamp.FromDateTimeOffset(deadline);

        await servicePort.ExecuteAsync(serviceRequest);

        invocation = serviceInvocationPort.Requests.Should().ContainSingle().Subject;
        invocation.ServiceRunCompletionNotificationTarget.ExpiresAtUnixMs.Should().Be(deadline.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ExecuteAsync_WithoutDeadline_ShouldUseNonExpiringWorkflowCompletionTarget()
    {
        var invocationPort = new RecordingInvocationPort();
        var port = new ValidatedWorkOrderExecutionPort(
            WorkOrderAssignmentValidatorTests.CreateValidator(),
            invocationPort);
        var request = BuildExecutionRequest();
        request.DeadlineAtUtc = null;

        await port.ExecuteAsync(request);

        var invocation = invocationPort.Requests.Should().ContainSingle().Subject;
        invocation.WorkflowCompletionNotificationTarget.ExpiresAtUnixMs.Should().Be(long.MaxValue);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutDeadline_ShouldUseNonExpiringServiceRunCompletionTarget()
    {
        var invocationPort = new RecordingInvocationPort();
        var port = new ValidatedWorkOrderExecutionPort(
            WorkOrderAssignmentValidatorTests.CreateValidator(
                workflowId: null,
                implementationKind: MemberImplementationKindNames.GAgent),
            invocationPort);
        var request = BuildExecutionRequest();
        request.WorkflowId = string.Empty;
        request.ImplementationKind = MemberImplementationKindNames.GAgent;
        request.DeadlineAtUtc = null;

        await port.ExecuteAsync(request);

        var invocation = invocationPort.Requests.Should().ContainSingle().Subject;
        invocation.ServiceRunCompletionNotificationTarget.ExpiresAtUnixMs.Should().Be(long.MaxValue);
    }

    private static WorkOrderExecutionRequest BuildExecutionRequest() =>
        new()
        {
            WorkOrderActorId = "work-order:scope-1:wo-1",
            WorkOrderId = "wo-1",
            ScopeId = "scope-1",
            TeamId = "team-1",
            MemberId = "member-1",
            PublishedServiceId = "service-1",
            WorkflowId = "workflow-1",
            ServiceRevisionId = "revision-1",
            ImplementationKind = MemberImplementationKindNames.Workflow,
            EndpointId = "run",
            Input = new WorkOrderServiceInput
            {
                Chat = new WorkOrderChatInput { Prompt = "do the work" },
            },
            DispatchCommandId = "command-1",
            RequestedRunId = "run-1",
            TerminalDeliveryId = "delivery-1",
            DeadlineAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(1)),
        };

    private sealed class RecordingInvocationPort : IServiceInvocationPort
    {
        public List<ServiceInvocationRequest> Requests { get; } = [];

        public string ReceiptRunId { get; init; } = "run-1";
        public string ReceiptCorrelationId { get; init; } = "command-1";
        public string ReceiptTargetActorId { get; init; } = "workflow-run-actor-1";
        public string ReceiptDeploymentId { get; init; } = "deployment-1";

        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request.Clone());
            return Task.FromResult(new ServiceInvocationAcceptedReceipt
            {
                RunId = ReceiptRunId,
                TargetActorId = ReceiptTargetActorId,
                CommandId = request.CommandId,
                CorrelationId = ReceiptCorrelationId,
                DeploymentId = ReceiptDeploymentId,
            });
        }
    }
}

public sealed class WorkOrderExecutionInfrastructureTests
{
    private const string ScopeId = "scope-1";
    private const string DedupKey = "worker-timeout";
    private static readonly string WorkOrderId = WorkOrderConventions.BuildWorkOrderId(ScopeId, DedupKey);
    private static readonly string ActorId = WorkOrderConventions.BuildActorId(ScopeId, WorkOrderId);
    private static readonly MethodInfo SetIdMethod = typeof(GAgentBase)
        .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GAgentBase.SetId was not found.");

    [Fact]
    public async Task WorkOrderExecutionQueue_WhenFull_ShouldThrowWithoutBlockingAndRetainClone()
    {
        var queue = new WorkOrderExecutionQueue(
            Options.Create(new WorkOrderExecutionWorkerOptions { QueueCapacity = 1 }));
        var original = BuildExecutionRequest("work-order-1", "command-1");
        queue.Enqueue(original);
        original.WorkOrderId = "mutated-after-enqueue";

        var overflow = await Task.Run(() =>
                Record.Exception(() => queue.Enqueue(BuildExecutionRequest("work-order-2", "command-2"))))
            .WaitAsync(TimeSpan.FromSeconds(5));

        overflow.Should().BeOfType<WorkOrderExecutionQueueFullException>()
            .Which.Message.Should().Match("*work-order-2*command-2*");
        await using var reader = queue.DequeueAllAsync().GetAsyncEnumerator();
        (await reader.MoveNextAsync()).Should().BeTrue();
        reader.Current.Should().NotBeSameAs(original);
        reader.Current.WorkOrderId.Should().Be("work-order-1");
        reader.Current.DispatchCommandId.Should().Be("command-1");
    }

    [Fact]
    public async Task WorkOrderExecutionQueue_WhenAdmissionCloses_ShouldRejectSynchronouslyAndDrainAcceptedWork()
    {
        var queue = new WorkOrderExecutionQueue(
            Options.Create(new WorkOrderExecutionWorkerOptions { QueueCapacity = 2 }));
        queue.Enqueue(BuildExecutionRequest("work-order-before-stop", "command-before-stop"));

        queue.CompleteAdding();
        var rejected = Record.Exception(() =>
            queue.Enqueue(BuildExecutionRequest("work-order-after-stop", "command-after-stop")));

        rejected.Should().BeOfType<WorkOrderExecutionQueueFullException>()
            .Which.Message.Should().Match("*work-order-after-stop*command-after-stop*");
        await using var reader = queue.DequeueAllAsync().GetAsyncEnumerator();
        (await reader.MoveNextAsync()).Should().BeTrue();
        reader.Current.WorkOrderId.Should().Be("work-order-before-stop");
        (await reader.MoveNextAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task WorkOrderExecutionWorker_WhenStopped_ShouldCloseQueueAdmission()
    {
        var queue = new WorkOrderExecutionQueue(
            Options.Create(new WorkOrderExecutionWorkerOptions { QueueCapacity = 1 }));
        var dispatch = new RecordingActorDispatchPort();
        using var worker = new WorkOrderExecutionWorker(
            queue,
            new WorkOrderExecutionService(
                new StubExecutionPort((_, _) => Task.FromResult(BuildAcceptedResult())),
                dispatch),
            Options.Create(new WorkOrderExecutionWorkerOptions
            {
                MaxConcurrency = 1,
                ShutdownDrainGraceSeconds = 1,
            }),
            NullLogger<WorkOrderExecutionWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        queue.Enqueue(BuildExecutionRequest("work-order-before-stop", "command-before-stop"));
        await dispatch.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await worker.StopAsync(CancellationToken.None);
        var rejected = Record.Exception(() =>
            queue.Enqueue(BuildExecutionRequest("work-order-late", "command-late")));

        rejected.Should().BeOfType<WorkOrderExecutionQueueFullException>()
            .Which.Message.Should().Match("*work-order-late*command-late*");
    }

    [Fact]
    public async Task WorkOrderExecutionService_WhenAccepted_ShouldDispatchAcceptedContinuation()
    {
        var accepted = BuildAcceptedResult();
        var dispatch = new RecordingActorDispatchPort();
        var service = new WorkOrderExecutionService(
            new StubExecutionPort((_, _) => Task.FromResult(accepted)),
            dispatch);
        var request = BuildExecutionRequest("work-order-1", "command-1");

        await service.ExecuteAsync(request);

        var call = dispatch.Calls.Should().ContainSingle().Subject;
        call.ActorId.Should().Be(request.WorkOrderActorId);
        call.Envelope.Id.Should().Be("work-order-execution-result:work-order-1:command-1");
        call.Envelope.Propagation.CorrelationId.Should().Be(call.Envelope.Id);
        call.Envelope.Route.PublisherActorId.Should().Be("studio.work-order-execution-worker");
        call.Envelope.Route.GetTargetActorId().Should().Be(request.WorkOrderActorId);
        var continuation = call.Envelope.Payload.Unpack<WorkOrderExecutionAcceptedContinuation>();
        continuation.WorkOrderId.Should().Be(request.WorkOrderId);
        continuation.DispatchCommandId.Should().Be(request.DispatchCommandId);
        continuation.RequestedRunId.Should().Be(request.RequestedRunId);
        continuation.Accepted.Should().BeEquivalentTo(accepted.Accepted);
    }

    [Fact]
    public async Task WorkOrderExecutionService_WhenUnexpectedFailure_ShouldDispatchSafeFailedContinuation()
    {
        const string sensitiveMessage = "credential secret must not escape";
        var dispatch = new RecordingActorDispatchPort();
        var service = new WorkOrderExecutionService(
            new StubExecutionPort((_, _) => throw new InvalidOperationException(sensitiveMessage)),
            dispatch);

        await service.ExecuteAsync(BuildExecutionRequest("work-order-1", "command-1"));

        var continuation = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload
            .Unpack<WorkOrderExecutionFailedContinuation>();
        continuation.Failed.Failure.Code.Should().Be("WORK_ORDER_EXECUTION_UNEXPECTED_FAILURE");
        continuation.Failed.Failure.Message.Should().Contain(nameof(InvalidOperationException));
        continuation.Failed.Failure.Message.Should().NotContain(sensitiveMessage);
    }

    [Fact]
    public async Task WorkOrderExecutionService_WhenContinuationDispatchFails_ShouldSurfaceForWatchdogRecovery()
    {
        var dispatch = new RecordingActorDispatchPort
        {
            DispatchException = new InvalidOperationException("continuation dispatch failed"),
        };
        var service = new WorkOrderExecutionService(
            new StubExecutionPort((_, _) => Task.FromResult(BuildAcceptedResult())),
            dispatch);

        var execute = () => service.ExecuteAsync(BuildExecutionRequest("work-order-1", "command-1"));

        await execute.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("continuation dispatch failed");
    }

    [Fact]
    public async Task BlockedWorker_ShouldNotBlockWorkOrderTimeout()
    {
        var queue = new WorkOrderExecutionQueue(
            Options.Create(new WorkOrderExecutionWorkerOptions { QueueCapacity = 4 }));
        var scheduler = new WorkOrderExecutionScheduler(queue);
        var blockedPort = new BlockingExecutionPort();
        using var worker = new WorkOrderExecutionWorker(
            queue,
            new WorkOrderExecutionService(blockedPort, new RecordingActorDispatchPort()),
            Options.Create(new WorkOrderExecutionWorkerOptions
            {
                MaxConcurrency = 1,
                ShutdownDrainGraceSeconds = 0,
            }),
            NullLogger<WorkOrderExecutionWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);

        try
        {
            var agent = await CreateExpiredDispatchPendingAgentAsync(scheduler);
            await agent.HandleExecuteAsync(new ExecuteWorkOrder
            {
                WorkOrderId = agent.State.WorkOrderId,
                DispatchCommandId = agent.State.DispatchCommandId,
            });
            await blockedPort.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await agent.HandleTimeoutAsync(new WorkOrderTimeoutFired
            {
                WorkOrderId = agent.State.WorkOrderId,
                TimeoutAtUtc = agent.State.TimeoutAtUtc.Clone(),
            });

            agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.TimedOut);
        }
        finally
        {
            blockedPort.Release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WorkerDispose_AfterShutdownGrace_ShouldNotDisposeSemaphoreWhileExecutionIsInFlight()
    {
        var queue = new WorkOrderExecutionQueue(
            Options.Create(new WorkOrderExecutionWorkerOptions { QueueCapacity = 1 }));
        var blockedPort = new BlockingExecutionPort();
        var dispatch = new RecordingActorDispatchPort();
        var worker = new WorkOrderExecutionWorker(
            queue,
            new WorkOrderExecutionService(blockedPort, dispatch),
            Options.Create(new WorkOrderExecutionWorkerOptions
            {
                MaxConcurrency = 1,
                ShutdownDrainGraceSeconds = 0,
            }),
            NullLogger<WorkOrderExecutionWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        queue.Enqueue(BuildExecutionRequest("work-order-1", "command-1"));

        try
        {
            await blockedPort.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            var rejected = Record.Exception(() =>
                queue.Enqueue(BuildExecutionRequest("work-order-late", "command-late")));

            var concurrency = (SemaphoreSlim)(typeof(WorkOrderExecutionWorker)
                .GetField("_concurrency", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(worker)!);
            var inspect = () => concurrency.Wait(0);

            inspect.Should().NotThrow(
                "late execution completion must be able to return its permit after shutdown grace");
            rejected.Should().BeOfType<WorkOrderExecutionQueueFullException>();
        }
        finally
        {
            blockedPort.Release.TrySetResult();
            await dispatch.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<WorkOrderGAgent> CreateExpiredDispatchPendingAgentAsync(
        IWorkOrderExecutionScheduler scheduler)
    {
        var agent = new WorkOrderGAgent(scheduler)
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkOrderState>(
                new InMemoryEventStore()),
            EventPublisher = new NoOpEventPublisher(),
            Services = new ServiceCollection()
                .AddSingleton<IEnumerable<IGAgentExecutionHook>>([])
                .AddSingleton<IActorRuntimeCallbackScheduler>(new NoOpCallbackScheduler())
                .BuildServiceProvider(),
        };
        SetIdMethod.Invoke(agent, [ActorId]);
        await agent.ActivateAsync();
        var requestedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        await agent.HandleCreateAsync(new CreateWorkOrder
        {
            WorkOrderId = WorkOrderId,
            DedupKey = DedupKey,
            ScopeId = ScopeId,
            TeamId = "team-1",
            Requester = new WorkOrderPrincipal
            {
                PrincipalId = "requester-1",
                PrincipalKind = "user",
            },
            MemberId = "member-1",
            PublishedServiceId = "service-1",
            WorkflowId = "workflow-1",
            ServiceRevisionId = "revision-1",
            ImplementationKind = MemberImplementationKindNames.Workflow,
            EndpointId = "run",
            Intent = "exercise timeout while worker is blocked",
            Input = new WorkOrderServiceInput
            {
                Chat = new WorkOrderChatInput { Prompt = "do the work" },
            },
            RequestedAtUtc = Timestamp.FromDateTimeOffset(requestedAt),
            TimeoutAtUtc = Timestamp.FromDateTimeOffset(requestedAt.AddMinutes(1)),
        });
        await agent.HandleDispatchAsync(new DispatchWorkOrder
        {
            WorkOrderId = WorkOrderId,
            ExpectedLifecycleVersion = 2,
            RequestedBy = new WorkOrderPrincipal
            {
                PrincipalId = "requester-1",
                PrincipalKind = "user",
            },
            DispatchCommandId = WorkOrderConventions.BuildDispatchCommandId(WorkOrderId),
            RequestedRunId = WorkOrderConventions.BuildRequestedRunId(WorkOrderId),
            TerminalDeliveryId = WorkOrderConventions.BuildTerminalDeliveryId(WorkOrderId),
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        return agent;
    }

    private static WorkOrderExecutionRequest BuildExecutionRequest(string workOrderId, string dispatchCommandId) =>
        new()
        {
            WorkOrderActorId = $"work-order:scope-1:{workOrderId}",
            WorkOrderId = workOrderId,
            ScopeId = ScopeId,
            TeamId = "team-1",
            MemberId = "member-1",
            PublishedServiceId = "service-1",
            WorkflowId = "workflow-1",
            ServiceRevisionId = "revision-1",
            ImplementationKind = MemberImplementationKindNames.Workflow,
            EndpointId = "run",
            Input = new WorkOrderServiceInput
            {
                Chat = new WorkOrderChatInput { Prompt = "do the work" },
            },
            DispatchCommandId = dispatchCommandId,
            RequestedRunId = $"run-{workOrderId}",
            TerminalDeliveryId = $"delivery-{workOrderId}",
            DeadlineAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(1)),
        };

    private static WorkOrderExecutionResult BuildAcceptedResult() =>
        new()
        {
            Accepted = new WorkOrderExecutionAccepted
            {
                RunId = "run-work-order-1",
                RunActorId = "workflow-run-actor-1",
                CommandId = "command-1",
                CorrelationId = "command-1",
                RevisionId = "revision-1",
                DeploymentId = "deployment-1",
                AcceptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

    private sealed class StubExecutionPort(
        Func<WorkOrderExecutionRequest, CancellationToken, Task<WorkOrderExecutionResult>> execute)
        : IWorkOrderExecutionPort
    {
        public Task<WorkOrderExecutionResult> ExecuteAsync(
            WorkOrderExecutionRequest request,
            CancellationToken ct = default) => execute(request, ct);
    }

    private sealed class BlockingExecutionPort : IWorkOrderExecutionPort
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkOrderExecutionResult> ExecuteAsync(
            WorkOrderExecutionRequest request,
            CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            return new WorkOrderExecutionResult
            {
                Failed = new WorkOrderExecutionFailed
                {
                    Failure = new WorkOrderFailureReference
                    {
                        Code = "TEST_RELEASED",
                        Message = "test execution released",
                        Source = "test",
                        ReferenceId = request.DispatchCommandId,
                    },
                    FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            };
        }
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<DispatchCall> Calls { get; } = [];
        public Exception? DispatchException { get; init; }
        public TaskCompletionSource Dispatched { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            if (DispatchException != null)
                throw DispatchException;

            Calls.Add(new DispatchCall(actorId, envelope.Clone()));
            Dispatched.TrySetResult();
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class NoOpCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoOpEventPublisher : IEventPublisher
    {
        public Task PublishAsync<T>(
            T evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage => Task.CompletedTask;

        public Task SendToAsync<T>(
            string targetActorId,
            T evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage => Task.CompletedTask;
    }

    private sealed record DispatchCall(string ActorId, EventEnvelope Envelope);
}
