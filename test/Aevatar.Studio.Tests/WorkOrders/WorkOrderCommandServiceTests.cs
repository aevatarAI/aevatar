using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.WorkOrder;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.CommandServices;
using FluentAssertions;

namespace Aevatar.Studio.Tests.WorkOrders;

public sealed class WorkOrderCommandServiceTests
{
    private const string ScopeId = "scope-1";

    [Fact]
    public async Task CreateAsync_ShouldUseStableLogicalIdentityAndAcceptedReceipt()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatchPort = new RecordingDispatchPort();
        var service = new ActorDispatchWorkOrderCommandService(
            bootstrap,
            CreateCommandDispatch(dispatchPort));
        var request = CreateRequest();
        var requester = new WorkOrderPrincipalContract("requester-1", "user");
        var assignment = CreateAssignment();
        var expectedWorkOrderId = WorkOrderConventions.BuildWorkOrderId(ScopeId, request.DedupKey);

        var first = await service.CreateAsync(ScopeId, request, requester, assignment);
        var second = await service.CreateAsync(ScopeId, request, requester, assignment);

        first.WorkOrderId.Should().Be(expectedWorkOrderId);
        first.CommandId.Should().StartWith($"work-order-create-{expectedWorkOrderId}-v0-");
        first.CorrelationId.Should().Be(first.CommandId);
        first.Stage.Should().Be(WorkOrderCommandStageNames.DispatchAccepted);
        second.Should().BeEquivalentTo(first, options => options.Excluding(receipt => receipt.AcceptedAtUtc));
        bootstrap.ActorIds.Should().Equal(
            WorkOrderConventions.BuildActorId(ScopeId, expectedWorkOrderId),
            WorkOrderConventions.BuildActorId(ScopeId, expectedWorkOrderId));
        dispatchPort.Envelopes.Select(static envelope => envelope.Id).Should().OnlyContain(
            commandId => commandId == first.CommandId);
        dispatchPort.Envelopes.Select(static envelope =>
                envelope.EnsureRuntime().EnsureDeliveryIdentity().OperationId)
            .Should().OnlyContain(operationId => operationId == first.CommandId);
    }

    [Fact]
    public async Task MateriallyDifferentCreateRequests_ShouldNotShareRuntimeDeliveryIdentity()
    {
        var dispatchPort = new RecordingDispatchPort();
        var service = new ActorDispatchWorkOrderCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatchPort));
        var original = CreateRequest();
        var conflicting = original with { Intent = "Produce a different report" };
        var requester = new WorkOrderPrincipalContract("requester-1", "user");
        var assignment = CreateAssignment();

        await service.CreateAsync(ScopeId, original, requester, assignment);
        await service.CreateAsync(ScopeId, conflicting, requester, assignment);

        dispatchPort.Envelopes.Select(static envelope => envelope.Id)
            .Should().OnlyHaveUniqueItems();
        dispatchPort.Envelopes.Select(static envelope =>
                envelope.EnsureRuntime().EnsureDeliveryIdentity().OperationId)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task DispatchAsync_ShouldCarryStableRunAndTerminalDeliveryIdentities()
    {
        var dispatchPort = new RecordingDispatchPort();
        var service = new ActorDispatchWorkOrderCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatchPort));
        var workOrderId = WorkOrderConventions.BuildWorkOrderId(ScopeId, "dedup-1");

        var receipt = await service.DispatchAsync(
            ScopeId,
            workOrderId,
            new DispatchWorkOrderRequest(ExpectedLifecycleVersion: 4),
            new WorkOrderPrincipalContract("requester-1", "user"));

        receipt.CommandId.Should().StartWith($"work-order-dispatch-{workOrderId}-v4-");
        var command = dispatchPort.Envelopes.Should().ContainSingle().Subject.Payload!
            .Unpack<DispatchWorkOrder>();
        command.DispatchCommandId.Should().Be(WorkOrderConventions.BuildDispatchCommandId(workOrderId));
        command.RequestedRunId.Should().Be(WorkOrderConventions.BuildRequestedRunId(workOrderId));
        command.TerminalDeliveryId.Should().Be(WorkOrderConventions.BuildTerminalDeliveryId(workOrderId));
        new[] { command.DispatchCommandId, command.RequestedRunId, command.TerminalDeliveryId }
            .Should().OnlyHaveUniqueItems();
    }

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

    [Fact]
    public async Task CreateAsync_WhenDeadlineElapsed_ShouldRejectBeforeActorDispatch()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatchPort = new RecordingDispatchPort();
        var service = new ActorDispatchWorkOrderCommandService(
            bootstrap,
            CreateCommandDispatch(dispatchPort));

        var create = () => service.CreateAsync(
            ScopeId,
            CreateRequest() with { TimeoutAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new WorkOrderPrincipalContract("requester-1", "user"),
            CreateAssignment());

        await create.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*deadline*later*request time*");
        bootstrap.ActorIds.Should().BeEmpty();
        dispatchPort.Envelopes.Should().BeEmpty();
    }

    private static CreateWorkOrderRequest CreateRequest() =>
        new(
            TeamId: "team-1",
            MemberId: "member-1",
            PublishedServiceId: "service-1",
            EndpointId: "chat",
            Intent: "Produce the report",
            DedupKey: "dedup-1",
            Input: new WorkOrderServiceInputContract(
                new WorkOrderChatInputContract("Create it")),
            TimeoutAtUtc: DateTimeOffset.UtcNow.AddHours(1));

    private static WorkOrderValidatedAssignment CreateAssignment() =>
        new(
            MemberId: "member-1",
            PublishedServiceId: "service-1",
            WorkflowId: "workflow-1",
            ServiceRevisionId: "revision-1",
            ImplementationKind: "workflow");

    private static StudioProjectionActorCommandDispatch CreateCommandDispatch(
        IActorDispatchPort dispatchPort) =>
        new(new DefaultCommandDispatchService<
            StudioProjectionActorCommand,
            StudioProjectionActorCommandTarget,
            StudioProjectionActorCommandReceipt,
            StudioProjectionActorCommandStartError>(
            new DefaultCommandDispatchPipeline<
                StudioProjectionActorCommand,
                StudioProjectionActorCommandTarget,
                StudioProjectionActorCommandReceipt,
                StudioProjectionActorCommandStartError>(
                new StudioProjectionActorCommandTargetResolver(),
                new DefaultCommandContextPolicy(),
                new StudioProjectionActorCommandEnvelopeFactory(),
                new ActorCommandTargetDispatcher<StudioProjectionActorCommandTarget>(dispatchPort),
                new StudioProjectionActorCommandReceiptFactory())));

    private sealed class RecordingBootstrap : IStudioActorBootstrap
    {
        public List<string> ActorIds { get; } = [];

        public Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor
        {
            ActorIds.Add(actorId);
            return Task.FromResult<IActor>(new StubActor(actorId));
        }
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<EventEnvelope> Envelopes { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Envelopes.Add(envelope);
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent => throw new NotSupportedException();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
