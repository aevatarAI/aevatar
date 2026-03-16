using Aevatar.Workflow.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowActorBindingProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldCaptureDefinitionBinding()
    {
        var dispatcher = new FakeStoreDispatcher();
        var projector = new WorkflowActorBindingProjector(
            dispatcher,
            dispatcher,
            new StaticClock(new DateTimeOffset(2026, 3, 14, 12, 0, 0, TimeSpan.Zero)));
        var context = new WorkflowBindingProjectionContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-binding",
        };

        await projector.ProjectAsync(
            context,
            WrapCommitted(
                new BindWorkflowDefinitionEvent
                {
                    WorkflowName = " direct ",
                    WorkflowYaml = "name: direct",
                    InlineWorkflowYamls =
                    {
                        [" child "] = "yaml-child",
                    },
                },
                version: 1,
                id: "evt-definition",
                utcTimestamp: new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc)),
            CancellationToken.None);

        var document = dispatcher.Documents["actor-1"];
        document.ActorKind.Should().Be(WorkflowActorKind.Definition);
        document.DefinitionActorId.Should().Be("actor-1");
        document.RunId.Should().BeEmpty();
        document.WorkflowName.Should().Be("direct");
        document.WorkflowYaml.Should().Be("name: direct");
        document.InlineWorkflowYamls.Should().ContainKey("child").WhoseValue.Should().Be("yaml-child");
        document.LastEventId.Should().Be("evt-definition");
    }

    [Fact]
    public async Task ProjectAsync_ShouldCaptureRunBinding_AndNormalizeRunId()
    {
        var dispatcher = new FakeStoreDispatcher();
        var projector = new WorkflowActorBindingProjector(dispatcher, dispatcher, new StaticClock(DateTimeOffset.UtcNow));
        var context = new WorkflowBindingProjectionContext
        {
            RootActorId = "actor-2",
            ProjectionKind = "workflow-binding",
        };

        await projector.ProjectAsync(
            context,
            WrapCommitted(
                new BindWorkflowRunDefinitionEvent
                {
                    DefinitionActorId = "definition-2",
                    RunId = " run-2 ",
                    WorkflowName = " auto ",
                    WorkflowYaml = "name: auto",
                    InlineWorkflowYamls =
                    {
                        [" child "] = "yaml-child",
                    },
                },
                version: 2,
                id: "evt-run"),
            CancellationToken.None);

        var document = dispatcher.Documents["actor-2"];
        document.ActorKind.Should().Be(WorkflowActorKind.Run);
        document.DefinitionActorId.Should().Be("definition-2");
        document.RunId.Should().Be("run-2");
        document.WorkflowName.Should().Be("auto");
        document.InlineWorkflowYamls.Should().ContainKey("child");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreUnrelatedEvents()
    {
        var dispatcher = new FakeStoreDispatcher();
        var projector = new WorkflowActorBindingProjector(dispatcher, dispatcher, new StaticClock(DateTimeOffset.UtcNow));
        var context = new WorkflowBindingProjectionContext
        {
            RootActorId = "actor-3",
            ProjectionKind = "workflow-binding",
        };

        await projector.ProjectAsync(
            context,
            WrapCommitted(
                new WorkflowCompletedEvent
                {
                    WorkflowName = "ignored",
                    Success = true,
                },
                version: 3,
                id: "evt-ignored"),
            CancellationToken.None);

        dispatcher.Documents.Should().BeEmpty();
    }

    private sealed class FakeStoreDispatcher
        : IProjectionWriteDispatcher<WorkflowActorBindingDocument>,
          IProjectionDocumentReader<WorkflowActorBindingDocument, string>
    {
        public Dictionary<string, WorkflowActorBindingDocument> Documents { get; } = new(StringComparer.Ordinal);

        public Task<ProjectionWriteResult> UpsertAsync(WorkflowActorBindingDocument readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Documents[readModel.Id] = readModel.DeepClone();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<WorkflowActorBindingDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Documents.TryGetValue(key, out var document) ? document.DeepClone() : null);
        }

        public Task<ProjectionDocumentQueryResult<WorkflowActorBindingDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectionDocumentQueryResult<WorkflowActorBindingDocument>
            {
                Items = Documents.Values
                    .Take(query.Take <= 0 ? 50 : query.Take)
                    .Select(static x => x.DeepClone())
                    .ToList(),
            });
        }
    }

    private sealed class StaticClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private static EventEnvelope WrapCommitted(
        IMessage evt,
        long version,
        string id,
        DateTime? utcTimestamp = null)
    {
        var occurredAt = Timestamp.FromDateTime((utcTimestamp ?? DateTime.UtcNow).ToUniversalTime());
        return new EventEnvelope
        {
            Id = id,
            Timestamp = occurredAt.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("binding-test"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = id,
                    Version = version,
                    Timestamp = occurredAt,
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(new Empty()),
            }),
        };
    }
}
