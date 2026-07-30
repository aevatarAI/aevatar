using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowConnectorApprovalProjectionTests
{
    [Fact]
    public async Task ProjectAndMap_ShouldExposeAuthoritativeConnectorApprovalSnapshots()
    {
        var connectorState = new ConnectorCallModuleState();
        connectorState.ApprovalsByActionId["action-b"] = Coordination(
            "action-b",
            WorkflowExternalActionLifecycleStatus.WaitingApproval,
            WorkflowExternalActionApprovalStatus.Pending,
            WorkflowExternalActionExecutionStatus.NotStarted);
        connectorState.ApprovalsByActionId["action-a"] = Coordination(
            "action-a",
            WorkflowExternalActionLifecycleStatus.Succeeded,
            WorkflowExternalActionApprovalStatus.Approved,
            WorkflowExternalActionExecutionStatus.Succeeded);
        var state = new WorkflowRunState
        {
            RunId = "run-approval",
            WorkflowName = "connector-approval",
            ScopeId = "scope-alpha",
            Status = "running",
            ExecutionStates =
            {
                ["connector_call"] = Any.Pack(connectorState),
            },
        };
        var dispatcher = new RecordingWriteDispatcher<WorkflowExecutionCurrentStateDocument>();
        var projector = new WorkflowExecutionCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 7, 17, 8, 1, 0, TimeSpan.Zero)));

        await projector.ProjectAsync(
            new WorkflowExecutionMaterializationContext
            {
                RootActorId = "run-approval",
                ProjectionKind = "workflow",
            },
            WrapCommitted(state));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.ConnectorApprovals.Select(static approval => approval.Plan.ActionId)
            .Should().ContainInOrder("action-a", "action-b");
        document.ConnectorApprovals[0].LifecycleStatus
            .Should().Be(WorkflowExternalActionLifecycleStatus.Succeeded);
        document.ConnectorApprovals[0].ExecutionStatus
            .Should().Be(WorkflowExternalActionExecutionStatus.Succeeded);
        document.ConnectorApprovals[1].ApprovalStatus
            .Should().Be(WorkflowExternalActionApprovalStatus.Pending);

        var snapshot = new WorkflowExecutionReadModelMapper().ToActorSnapshot(document);

        snapshot.ConnectorApprovals.Should().HaveCount(2);
        snapshot.ConnectorApprovals[0].Plan.ActionId.Should().Be("action-a");
        snapshot.ConnectorApprovals[0].Plan.Provenance.PrincipalSubject.Should().Be("user-alpha");
        snapshot.ConnectorApprovals[1].Plan.NodeId.Should().Be("node-alpha");
        document.ConnectorApprovals[0].Plan.Summary = "mutated-after-map";
        snapshot.ConnectorApprovals[0].Plan.Summary.Should().Be("POST /resources/action-a");
    }

    private static ConnectorApprovalCoordinationState Coordination(
        string actionId,
        WorkflowExternalActionLifecycleStatus lifecycleStatus,
        WorkflowExternalActionApprovalStatus approvalStatus,
        WorkflowExternalActionExecutionStatus executionStatus) =>
        new()
        {
            Snapshot = new WorkflowExternalActionApprovalSnapshot
            {
                Plan = new WorkflowExternalActionPlan
                {
                    ActionId = actionId,
                    Summary = $"POST /resources/{actionId}",
                    ServiceRef = "service-alpha",
                    NodeId = "node-alpha",
                    Operation = "create_resource",
                    HttpVerb = "POST",
                    Resource = $"/resources/{actionId}",
                    PermissionScope = "resources.write",
                    Provenance = new WorkflowExternalActionProvenance
                    {
                        ScopeId = "scope-alpha",
                        RunId = "run-approval",
                        StepId = "connector-approval",
                        PrincipalSubject = "user-alpha",
                    },
                },
                LifecycleStatus = lifecycleStatus,
                ApprovalStatus = approvalStatus,
                ExecutionStatus = executionStatus,
            },
        };

    private static EventEnvelope WrapCommitted(WorkflowRunState state) =>
        new()
        {
            Id = "evt-connector-approval",
            Timestamp = Timestamp.FromDateTime(new DateTime(2026, 7, 17, 8, 1, 0, DateTimeKind.Utc)),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("run-approval"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-connector-approval",
                    Version = 11,
                    EventData = Any.Pack(new WorkflowExecutionStateUpsertedEvent
                    {
                        ScopeKey = "connector_call",
                    }),
                },
                StateRoot = Any.Pack(state),
            }),
        };

    private sealed class RecordingWriteDispatcher<TReadModel> : IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        public List<TReadModel> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
