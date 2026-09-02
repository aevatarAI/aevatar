using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.WorkOrder;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests.WorkOrders;

public sealed class WorkOrderAuthorityBoundaryTests
{
    private const string ScopeId = "scope-1";
    private const string DedupKey = "durable-intent-1";
    private static readonly string WorkOrderId = WorkOrderConventions.BuildWorkOrderId(ScopeId, DedupKey);
    private static readonly string ActorId = WorkOrderConventions.BuildActorId(ScopeId, WorkOrderId);
    private static readonly MethodInfo SetIdMethod = typeof(GAgentBase)
        .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GAgentBase.SetId was not found.");

    [Fact]
    public async Task WorkOrderWithoutDeadline_ShouldSurviveReassignmentRecoveryAndCancellationBeforeAnyRun()
    {
        var eventStore = new InMemoryEventStore();
        var created = await CreateAgentAsync(eventStore);

        await created.HandleCreateAsync(BuildCreate(timeoutAtUtc: null));

        created.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Ready);
        created.State.TimeoutAtUtc.Should().BeNull();
        created.State.Run.Should().BeNull();

        await created.HandleReassignAsync(BuildReassign(created.State.LifecycleVersion));

        var recovered = await CreateAgentAsync(eventStore);
        recovered.State.MemberId.Should().Be("member-2");
        recovered.State.PublishedServiceId.Should().Be("service-2");
        recovered.State.Run.Should().BeNull();

        await recovered.HandleCancelAsync(BuildCancel(recovered.State.LifecycleVersion));

        var terminal = await CreateAgentAsync(eventStore);
        terminal.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Cancelled);
        terminal.State.Run.Should().BeNull();
    }

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
            .Should().Equal(
                "delivery_id",
                "run_id",
                "run_actor_id",
                "command_id",
                "correlation_id",
                "outcome",
                "terminal_at_utc");
    }

    private static async Task<WorkOrderGAgent> CreateAgentAsync(InMemoryEventStore eventStore)
    {
        var agent = new WorkOrderGAgent
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkOrderState>(eventStore),
            EventPublisher = new NoOpEventPublisher(),
            Services = new ServiceCollection()
                .AddSingleton<IEnumerable<IGAgentExecutionHook>>([])
                .AddSingleton<IActorRuntimeCallbackScheduler>(new NoOpCallbackScheduler())
                .BuildServiceProvider(),
        };
        SetIdMethod.Invoke(agent, [ActorId]);
        await agent.ActivateAsync();
        return agent;
    }

    private static CreateWorkOrder BuildCreate(Timestamp? timeoutAtUtc)
    {
        var requestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        return new CreateWorkOrder
        {
            WorkOrderId = WorkOrderId,
            DedupKey = DedupKey,
            ScopeId = ScopeId,
            TeamId = "team-1",
            Requester = Principal("requester-1"),
            MemberId = "member-1",
            PublishedServiceId = "service-1",
            WorkflowId = "workflow-1",
            ServiceRevisionId = "revision-1",
            ImplementationKind = "workflow",
            EndpointId = "run",
            Intent = "Complete the durable work",
            Input = new WorkOrderServiceInput
            {
                Chat = new WorkOrderChatInput { Prompt = "Do the work" },
            },
            RequestedAtUtc = requestedAt,
            TimeoutAtUtc = timeoutAtUtc,
            ExpectedLifecycleVersion = 0,
        };
    }

    private static ReassignWorkOrder BuildReassign(long expectedLifecycleVersion) =>
        new()
        {
            WorkOrderId = WorkOrderId,
            ExpectedLifecycleVersion = expectedLifecycleVersion,
            RequestedBy = Principal("requester-1"),
            MemberId = "member-2",
            PublishedServiceId = "service-2",
            WorkflowId = "workflow-2",
            ServiceRevisionId = "revision-2",
            ImplementationKind = "workflow",
        };

    private static CancelWorkOrder BuildCancel(long expectedLifecycleVersion) =>
        new()
        {
            WorkOrderId = WorkOrderId,
            ExpectedLifecycleVersion = expectedLifecycleVersion,
            RequestedBy = Principal("requester-1"),
            Reason = "No longer needed",
        };

    private static WorkOrderPrincipal Principal(string principalId) =>
        new()
        {
            PrincipalId = principalId,
            PrincipalKind = "user",
        };

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
}
