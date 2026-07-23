using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.WorkOrder;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Projection.DependencyInjection;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests.WorkOrders;

public sealed class WorkOrderProjectionTests
{
    private const string ScopeId = "scope-1";
    private const string WorkOrderId = "wo-1";
    private static readonly string ActorId = WorkOrderConventions.BuildActorId(ScopeId, WorkOrderId);

    [Fact]
    public void ReadModelProviders_ShouldRegisterWorkOrderStore()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddStudioProjectionComponents();
        services.AddStudioProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionDocumentReader<WorkOrderCurrentStateDocument, string>>()
            .Should().BeOfType<InMemoryProjectionDocumentStore<WorkOrderCurrentStateDocument, string>>();
        provider.GetRequiredService<IProjectionDocumentWriter<WorkOrderCurrentStateDocument>>()
            .Should().BeOfType<InMemoryProjectionDocumentStore<WorkOrderCurrentStateDocument, string>>();
    }

    [Fact]
    public async Task ProjectAsync_ShouldPreserveAuthoritativeWorkOrderUpdatedAt()
    {
        var projectionObservedAt = DateTimeOffset.Parse("2026-07-17T10:00:00Z");
        var workOrderUpdatedAt = DateTimeOffset.Parse("2026-07-17T09:00:00Z");
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new WorkOrderCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(projectionObservedAt));
        var state = BuildState(workOrderUpdatedAt);

        await projector.ProjectAsync(
            new StudioMaterializationContext
            {
                RootActorId = ActorId,
                ProjectionKind = WorkOrderGAgent.ProjectionKind,
            },
            WrapCommitted(state, projectionObservedAt));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.ActorId.Should().Be(ActorId);
        document.StateVersion.Should().Be(7);
        document.WorkOrderId.Should().Be(WorkOrderId);
        document.RequesterPrincipalId.Should().Be("requester-1");
        document.MemberId.Should().Be("member-1");
        document.WorkflowId.Should().Be("workflow-1");
        document.PublishedServiceId.Should().Be("service-1");
        document.RunId.Should().Be("run-1");
        document.RunAcceptedAtUnixMs.Should().Be(workOrderUpdatedAt.AddMinutes(-1).ToUnixTimeMilliseconds());
        document.RunOutcome!.CorrelationId.Should().Be("correlation-1");
        document.UpdatedAt!.ToDateTimeOffset().Should().Be(projectionObservedAt);
        document.WorkOrderUpdatedAtUtc!.ToDateTimeOffset().Should().Be(workOrderUpdatedAt);
    }

    [Fact]
    public async Task ProjectAsync_ShouldNotInventRunOrDeadlineForPreDispatchWorkOrder()
    {
        var projectionObservedAt = DateTimeOffset.Parse("2026-07-17T10:00:00Z");
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new WorkOrderCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(projectionObservedAt));
        var state = BuildState(projectionObservedAt);
        state.LifecycleStatus = WorkOrderLifecycleStatus.Ready;
        state.Run = null;
        state.RunOutcome = null;
        state.LateRunOutcome = null;
        state.TimeoutAtUtc = null;

        await projector.ProjectAsync(
            new StudioMaterializationContext
            {
                RootActorId = ActorId,
                ProjectionKind = WorkOrderGAgent.ProjectionKind,
            },
            WrapCommitted(state, projectionObservedAt));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.LifecycleStatus.Should().Be(WorkOrderLifecycleStatusNames.Ready);
        document.RunId.Should().BeEmpty();
        document.RunActorId.Should().BeEmpty();
        document.RunCommandId.Should().BeEmpty();
        document.RunCorrelationId.Should().BeEmpty();
        document.RunRevisionId.Should().BeEmpty();
        document.RunDeploymentId.Should().BeEmpty();
        document.RunAcceptedAtUnixMs.Should().Be(0);
        document.RunOutcome.Should().BeNull();
        document.LateRunOutcome.Should().BeNull();
        document.TimeoutAtUnixMs.Should().Be(0);
    }

    [Theory]
    [InlineData(WorkOrderTerminalOutcome.Succeeded, "succeeded")]
    [InlineData(WorkOrderTerminalOutcome.Failed, "failed")]
    [InlineData(WorkOrderTerminalOutcome.Stopped, "stopped")]
    [InlineData(WorkOrderTerminalOutcome.Unspecified, "")]
    public async Task ProjectAsync_ShouldMapRunOutcomeToWireName(
        WorkOrderTerminalOutcome outcome,
        string expectedWireName)
    {
        var projectionObservedAt = DateTimeOffset.Parse("2026-07-17T10:00:00Z");
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new WorkOrderCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(projectionObservedAt));
        var state = BuildState(projectionObservedAt);
        state.RunOutcome!.Outcome = outcome;

        await projector.ProjectAsync(
            new StudioMaterializationContext
            {
                RootActorId = ActorId,
                ProjectionKind = WorkOrderGAgent.ProjectionKind,
            },
            WrapCommitted(state, projectionObservedAt));

        dispatcher.Upserts.Should().ContainSingle().Subject.RunOutcome!.Outcome
            .Should().Be(expectedWireName);
    }

    [Fact]
    public async Task ListAsync_ShouldFilterCurrentStateAndReturnAuthoritativeUpdatedAt()
    {
        var workOrderUpdatedAt = DateTimeOffset.Parse("2026-07-17T09:00:00Z");
        var document = new WorkOrderCurrentStateDocument
        {
            Id = ActorId,
            ActorId = ActorId,
            StateVersion = 7,
            LastEventId = "evt-7",
            UpdatedAt = Timestamp.FromDateTimeOffset(workOrderUpdatedAt.AddMinutes(5)),
            WorkOrderUpdatedAtUtc = Timestamp.FromDateTimeOffset(workOrderUpdatedAt),
            WorkOrderId = WorkOrderId,
            ScopeId = ScopeId,
            TeamId = "team-1",
            RequesterPrincipalId = "requester-1",
            RequesterPrincipalKind = "user",
            MemberId = "member-1",
            PublishedServiceId = "service-1",
            WorkflowId = "workflow-1",
            ServiceRevisionId = "revision-1",
            ImplementationKind = "workflow",
            EndpointId = "chat",
            Intent = "Produce the report",
            DedupKey = "dedup-1",
            LifecycleStatus = WorkOrderLifecycleStatusNames.Completed,
            LifecycleVersion = 5,
            CreatedAtUnixMs = workOrderUpdatedAt.AddHours(-1).ToUnixTimeMilliseconds(),
            RunId = "run-1",
            RunActorId = "run-1",
            RunCommandId = "command-1",
            RunCorrelationId = "correlation-1",
            RunRevisionId = "revision-1",
            RunDeploymentId = "deployment-1",
            RunAcceptedAtUnixMs = workOrderUpdatedAt.AddMinutes(-1).ToUnixTimeMilliseconds(),
            RunOutcome = new WorkOrderRunOutcomeReferenceDocument
            {
                DeliveryId = "delivery-1",
                RunId = "run-1",
                RunActorId = "run-1",
                CommandId = "command-1",
                CorrelationId = "correlation-1",
                Outcome = "succeeded",
                TerminalAtUnixMs = workOrderUpdatedAt.ToUnixTimeMilliseconds(),
            },
        };
        var reader = new RecordingDocumentReader(document);
        var port = new ProjectionWorkOrderQueryPort(reader);

        var result = await port.ListAsync(
            ScopeId,
            new WorkOrderQueryRequest(
                PageSize: 25,
                Status: WorkOrderLifecycleStatusNames.Completed,
                RequesterPrincipalId: "requester-1",
                TeamId: "team-1",
                MemberId: "member-1",
                PublishedServiceId: "service-1",
                WorkflowId: "workflow-1",
                RunId: "run-1",
                CreatedFromUtc: workOrderUpdatedAt.AddHours(-2),
                CreatedToUtc: workOrderUpdatedAt),
            CancellationToken.None);

        var response = result.WorkOrders.Should().ContainSingle().Subject;
        response.UpdatedAtUtc.Should().Be(workOrderUpdatedAt);
        response.Requester.PrincipalId.Should().Be("requester-1");
        response.MemberId.Should().Be("member-1");
        response.WorkflowId.Should().Be("workflow-1");
        response.PublishedServiceId.Should().Be("service-1");
        response.Run!.RunId.Should().Be("run-1");
        response.RunOutcome!.CorrelationId.Should().Be("correlation-1");
        reader.LastQuery!.Take.Should().Be(25);
        reader.LastQuery.Filters.Select(static filter => filter.FieldPath).Should().BeEquivalentTo(
            "scope_id",
            "lifecycle_status",
            "requester_principal_id",
            "team_id",
            "member_id",
            "published_service_id",
            "workflow_id",
            "run_id",
            "created_at_unix_ms",
            "created_at_unix_ms");
    }

    private static WorkOrderState BuildState(DateTimeOffset updatedAt) =>
        new()
        {
            WorkOrderId = WorkOrderId,
            DedupKey = "dedup-1",
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
            ImplementationKind = "workflow",
            EndpointId = "chat",
            Intent = "Produce the report",
            Input = new WorkOrderServiceInput
            {
                Chat = new WorkOrderChatInput { Prompt = "Create it" },
            },
            LifecycleStatus = WorkOrderLifecycleStatus.Completed,
            LifecycleVersion = 5,
            CreatedAtUtc = Timestamp.FromDateTimeOffset(updatedAt.AddHours(-1)),
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(updatedAt),
            Run = new WorkOrderRunLink
            {
                RunId = "run-1",
                RunActorId = "run-1",
                CommandId = "command-1",
                CorrelationId = "correlation-1",
                RevisionId = "revision-1",
                DeploymentId = "deployment-1",
                AcceptedAtUtc = Timestamp.FromDateTimeOffset(updatedAt.AddMinutes(-1)),
            },
            RunOutcome = new WorkOrderRunOutcomeReference
            {
                DeliveryId = "delivery-1",
                RunId = "run-1",
                RunActorId = "run-1",
                CommandId = "command-1",
                CorrelationId = "correlation-1",
                Outcome = WorkOrderTerminalOutcome.Succeeded,
                TerminalAtUtc = Timestamp.FromDateTimeOffset(updatedAt),
            },
        };

    private static EventEnvelope WrapCommitted(WorkOrderState state, DateTimeOffset observedAt) =>
        new()
        {
            Id = "evt-envelope-7",
            Timestamp = Timestamp.FromDateTimeOffset(observedAt),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(ActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-7",
                    Version = 7,
                    EventData = Any.Pack(new WorkOrderRunAcceptedEvent()),
                    Timestamp = Timestamp.FromDateTimeOffset(observedAt),
                },
                StateRoot = Any.Pack(state),
            }),
        };

    private sealed class RecordingWriteDispatcher
        : IProjectionWriteDispatcher<WorkOrderCurrentStateDocument>
    {
        public List<WorkOrderCurrentStateDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            WorkOrderCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());
    }

    private sealed class RecordingDocumentReader(WorkOrderCurrentStateDocument document)
        : IProjectionDocumentReader<WorkOrderCurrentStateDocument, string>
    {
        public ProjectionDocumentQuery? LastQuery { get; private set; }

        public Task<WorkOrderCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default) =>
            Task.FromResult<WorkOrderCurrentStateDocument?>(
                string.Equals(key, document.Id, StringComparison.Ordinal) ? document : null);

        public Task<ProjectionDocumentQueryResult<WorkOrderCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(new ProjectionDocumentQueryResult<WorkOrderCurrentStateDocument>
            {
                Items = [document],
            });
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
