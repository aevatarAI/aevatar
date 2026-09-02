using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

/// <summary>
/// Regression guard for the studio binding clobber: a run actor's BindWorkflowRunDefinitionEvent is
/// relayed UP to the parent definition actor's binding projection scope via the committed-observation
/// relay. The binding projector must never write a Run-kind document onto the definition actor's _id.
/// </summary>
public sealed class WorkflowActorBindingRelayClobberTests
{
    [Fact]
    public async Task ProjectAsync_ShouldSkipRelayedRunBind_SoDefinitionIdNeverHoldsRunDocument()
    {
        // RootActorId == the parent definition (the relay target); origin == the run that authored the
        // committed event. The relayed delivery must be skipped so the definition document is intact.
        var dispatcher = new FakeStoreDispatcher();
        var projector = new WorkflowActorBindingProjector(dispatcher, new StaticClock(DateTimeOffset.UtcNow));
        var definitionScope = new WorkflowBindingProjectionContext
        {
            RootActorId = "workflow-definition:studio",
            ProjectionKind = "workflow-binding",
        };

        await projector.ProjectAsync(
            definitionScope,
            WrapCommittedRunBind(
                runActorId: "workflow-definition:studio:run:abc",
                definitionActorId: "workflow-definition:studio",
                version: 701,
                id: "evt-relayed-run"),
            CancellationToken.None);

        dispatcher.Documents.Should().NotContainKey("workflow-definition:studio");
        dispatcher.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldWriteRunBinding_OnRunsOwnScope()
    {
        // RootActorId == origin actor id (the run's own binding scope): the Run document is materialized
        // keyed on the run's own actor id, never the parent definition.
        var dispatcher = new FakeStoreDispatcher();
        var projector = new WorkflowActorBindingProjector(dispatcher, new StaticClock(DateTimeOffset.UtcNow));
        var runScope = new WorkflowBindingProjectionContext
        {
            RootActorId = "workflow-definition:studio:run:abc",
            ProjectionKind = "workflow-binding",
        };

        await projector.ProjectAsync(
            runScope,
            WrapCommittedRunBind(
                runActorId: "workflow-definition:studio:run:abc",
                definitionActorId: "workflow-definition:studio",
                version: 1,
                id: "evt-own-run"),
            CancellationToken.None);

        dispatcher.Documents.Should().NotContainKey("workflow-definition:studio");
        var document = dispatcher.Documents["workflow-definition:studio:run:abc"];
        document.ActorKind.Should().Be(WorkflowActorKind.Run);
        document.DefinitionActorId.Should().Be("workflow-definition:studio");
        document.RunId.Should().Be("workflow-definition:studio:run:abc");
    }

    [Fact]
    public async Task ProjectAsync_ShouldWriteRunBinding_WhenOriginActorIdAbsent_ForLegacyCompatibility()
    {
        // Legacy/unstamped committed events without an origin actor id fall back to the current
        // RootActorId (preserves prior behavior); the skip-on-relay guard only fires when origin is known.
        var dispatcher = new FakeStoreDispatcher();
        var projector = new WorkflowActorBindingProjector(dispatcher, new StaticClock(DateTimeOffset.UtcNow));
        var context = new WorkflowBindingProjectionContext
        {
            RootActorId = "run-legacy",
            ProjectionKind = "workflow-binding",
        };

        await projector.ProjectAsync(
            context,
            WrapCommittedRunBind(
                runActorId: string.Empty,
                definitionActorId: "definition-legacy",
                version: 2,
                id: "evt-legacy"),
            CancellationToken.None);

        dispatcher.Documents["run-legacy"].ActorKind.Should().Be(WorkflowActorKind.Run);
    }

    private static EventEnvelope WrapCommittedRunBind(
        string runActorId,
        string definitionActorId,
        long version,
        string id)
    {
        var occurredAt = Timestamp.FromDateTime(DateTime.UtcNow);
        return new EventEnvelope
        {
            Id = id,
            Timestamp = occurredAt.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("binding-relay-test"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = id,
                    Version = version,
                    Timestamp = occurredAt,
                    AgentId = runActorId ?? string.Empty,
                    EventData = Any.Pack(new BindWorkflowRunDefinitionEvent
                    {
                        DefinitionActorId = definitionActorId,
                        RunId = string.IsNullOrEmpty(runActorId) ? "run-legacy" : runActorId,
                        WorkflowName = "studio",
                        WorkflowYaml = "name: studio",
                    }),
                },
                StateRoot = Any.Pack(new Empty()),
            }),
        };
    }

    private sealed class FakeStoreDispatcher : IProjectionWriteDispatcher<WorkflowActorBindingDocument>
    {
        public Dictionary<string, WorkflowActorBindingDocument> Documents { get; } = new(StringComparer.Ordinal);

        public Task<ProjectionWriteResult> UpsertAsync(WorkflowActorBindingDocument readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Documents[readModel.Id] = readModel.Clone();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var removed = Documents.Remove(id);
            return Task.FromResult(removed
                ? ProjectionWriteResult.Applied()
                : ProjectionWriteResult.Duplicate());
        }
    }

    private sealed class StaticClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
