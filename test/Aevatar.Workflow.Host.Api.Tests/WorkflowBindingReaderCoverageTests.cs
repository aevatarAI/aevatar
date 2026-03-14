using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
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
            new StaticClock(new DateTimeOffset(2026, 3, 14, 12, 0, 0, TimeSpan.Zero)));
        var context = new WorkflowBindingProjectionContext
        {
            ProjectionId = "actor-1:binding",
            RootActorId = "actor-1",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "evt-definition",
                Timestamp = Timestamp.FromDateTime(DateTime.SpecifyKind(new DateTime(2026, 3, 14, 12, 0, 0), DateTimeKind.Utc)),
                Payload = Any.Pack(new BindWorkflowDefinitionEvent
                {
                    WorkflowName = " direct ",
                    WorkflowYaml = "name: direct",
                    InlineWorkflowYamls =
                    {
                        [" child "] = "yaml-child",
                    },
                }),
            },
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
        var projector = new WorkflowActorBindingProjector(dispatcher, new StaticClock(DateTimeOffset.UtcNow));
        var context = new WorkflowBindingProjectionContext
        {
            ProjectionId = "actor-2:binding",
            RootActorId = "actor-2",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "evt-run",
                Payload = Any.Pack(new BindWorkflowRunDefinitionEvent
                {
                    DefinitionActorId = "definition-2",
                    RunId = " run-2 ",
                    WorkflowName = " auto ",
                    WorkflowYaml = "name: auto",
                    InlineWorkflowYamls =
                    {
                        [" child "] = "yaml-child",
                    },
                }),
            },
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
        var projector = new WorkflowActorBindingProjector(dispatcher, new StaticClock(DateTimeOffset.UtcNow));
        var context = new WorkflowBindingProjectionContext
        {
            ProjectionId = "actor-3:binding",
            RootActorId = "actor-3",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "evt-ignored",
                Payload = Any.Pack(new WorkflowCompletedEvent
                {
                    WorkflowName = "ignored",
                    Success = true,
                }),
            },
            CancellationToken.None);

        dispatcher.Documents.Should().BeEmpty();
    }

    private sealed class FakeStoreDispatcher : IProjectionStoreDispatcher<WorkflowActorBindingDocument, string>
    {
        public Dictionary<string, WorkflowActorBindingDocument> Documents { get; } = new(StringComparer.Ordinal);

        public Task UpsertAsync(WorkflowActorBindingDocument readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Documents[readModel.Id] = readModel.DeepClone();
            return Task.CompletedTask;
        }

        public Task MutateAsync(string key, Action<WorkflowActorBindingDocument> mutate, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!Documents.TryGetValue(key, out var document))
            {
                document = new WorkflowActorBindingDocument
                {
                    Id = key,
                    ActorId = key,
                };
            }

            mutate(document);
            Documents[key] = document.DeepClone();
            return Task.CompletedTask;
        }

        public Task<WorkflowActorBindingDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Documents.TryGetValue(key, out var document) ? document.DeepClone() : null);
        }

        public Task<IReadOnlyList<WorkflowActorBindingDocument>> ListAsync(int take = 50, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkflowActorBindingDocument>>(
                Documents.Values.Take(take).Select(static x => x.DeepClone()).ToList());
        }
    }

    private sealed class StaticClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
