using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowActorBindingHealingWriteDispatcherTests
{
    [Fact]
    public async Task UpsertAsync_ShouldClearStaleRunDocument_BeforeWritingDefinitionAtSameId()
    {
        // Simulates the live frozen state: a Run-kind document (frozen at a high run version) is
        // squatting on a definition actor's _id. The definition's own low-version Definition write
        // would be rejected as Stale by the generic version-only evaluator; the heal dispatcher must
        // delete the Run document first so the Definition write lands at its own true version.
        const string definitionId = "workflow-definition:studio";
        var inner = new FakeBindingWriteDispatcher();
        inner.Seed(new WorkflowActorBindingDocument
        {
            Id = definitionId,
            ActorId = definitionId,
            ActorKind = WorkflowActorKind.Run,
            StateVersion = 701,
            RunId = "workflow-definition:studio:run:abc",
        });
        var reader = new FakeBindingReader(inner);
        var dispatcher = new WorkflowActorBindingHealingWriteDispatcher(inner, reader);

        var result = await dispatcher.UpsertAsync(
            new WorkflowActorBindingDocument
            {
                Id = definitionId,
                ActorId = definitionId,
                ActorKind = WorkflowActorKind.Definition,
                DefinitionActorId = definitionId,
                StateVersion = 2,
                WorkflowName = "studio",
                WorkflowYaml = "name: studio",
            },
            CancellationToken.None);

        result.IsApplied.Should().BeTrue();
        inner.Deletes.Should().ContainSingle().Which.Should().Be(definitionId);
        var stored = inner.Documents[definitionId];
        stored.ActorKind.Should().Be(WorkflowActorKind.Definition);
        stored.StateVersion.Should().Be(2);
    }

    [Fact]
    public async Task UpsertAsync_ShouldNotDelete_WhenExistingIsAlreadyDefinition()
    {
        const string definitionId = "workflow-definition:auto";
        var inner = new FakeBindingWriteDispatcher();
        inner.Seed(new WorkflowActorBindingDocument
        {
            Id = definitionId,
            ActorId = definitionId,
            ActorKind = WorkflowActorKind.Definition,
            StateVersion = 1,
        });
        var reader = new FakeBindingReader(inner);
        var dispatcher = new WorkflowActorBindingHealingWriteDispatcher(inner, reader);

        await dispatcher.UpsertAsync(
            new WorkflowActorBindingDocument
            {
                Id = definitionId,
                ActorId = definitionId,
                ActorKind = WorkflowActorKind.Definition,
                StateVersion = 2,
            },
            CancellationToken.None);

        inner.Deletes.Should().BeEmpty();
        inner.Documents[definitionId].StateVersion.Should().Be(2);
    }

    [Fact]
    public async Task UpsertAsync_ShouldNotDelete_ForRunKindWrite()
    {
        // A Run-kind write keyed on the run's own id must pass through untouched (the heal only
        // applies when a Definition write meets a Run document).
        const string runId = "workflow-definition:studio:run:abc";
        var inner = new FakeBindingWriteDispatcher();
        var reader = new FakeBindingReader(inner);
        var dispatcher = new WorkflowActorBindingHealingWriteDispatcher(inner, reader);

        await dispatcher.UpsertAsync(
            new WorkflowActorBindingDocument
            {
                Id = runId,
                ActorId = runId,
                ActorKind = WorkflowActorKind.Run,
                StateVersion = 1,
            },
            CancellationToken.None);

        inner.Deletes.Should().BeEmpty();
        reader.ReadCount.Should().Be(0);
        inner.Documents[runId].ActorKind.Should().Be(WorkflowActorKind.Run);
    }

    [Fact]
    public async Task UpsertAsync_ShouldNotDelete_WhenSlotEmpty()
    {
        const string definitionId = "workflow-definition:fresh";
        var inner = new FakeBindingWriteDispatcher();
        var reader = new FakeBindingReader(inner);
        var dispatcher = new WorkflowActorBindingHealingWriteDispatcher(inner, reader);

        await dispatcher.UpsertAsync(
            new WorkflowActorBindingDocument
            {
                Id = definitionId,
                ActorId = definitionId,
                ActorKind = WorkflowActorKind.Definition,
                StateVersion = 1,
            },
            CancellationToken.None);

        inner.Deletes.Should().BeEmpty();
        inner.Documents[definitionId].ActorKind.Should().Be(WorkflowActorKind.Definition);
    }

    private sealed class FakeBindingWriteDispatcher
        : IProjectionWriteDispatcher<WorkflowActorBindingDocument>
    {
        public Dictionary<string, WorkflowActorBindingDocument> Documents { get; } = new(StringComparer.Ordinal);

        public List<string> Deletes { get; } = [];

        public void Seed(WorkflowActorBindingDocument document) =>
            Documents[document.Id] = document.Clone();

        public Task<ProjectionWriteResult> UpsertAsync(
            WorkflowActorBindingDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Documents[readModel.Id] = readModel.Clone();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Deletes.Add(id);
            var removed = Documents.Remove(id);
            return Task.FromResult(removed
                ? ProjectionWriteResult.Applied()
                : ProjectionWriteResult.Duplicate());
        }
    }

    private sealed class FakeBindingReader
        : IProjectionDocumentReader<WorkflowActorBindingDocument, string>
    {
        private readonly FakeBindingWriteDispatcher _store;

        public FakeBindingReader(FakeBindingWriteDispatcher store) => _store = store;

        public int ReadCount { get; private set; }

        public Task<WorkflowActorBindingDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ReadCount++;
            return Task.FromResult(_store.Documents.TryGetValue(key, out var document)
                ? document.Clone()
                : null);
        }

        public Task<ProjectionDocumentQueryResult<WorkflowActorBindingDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(new ProjectionDocumentQueryResult<WorkflowActorBindingDocument>
            {
                Items = [],
                NextCursor = null,
                TotalCount = null,
            });
    }
}
