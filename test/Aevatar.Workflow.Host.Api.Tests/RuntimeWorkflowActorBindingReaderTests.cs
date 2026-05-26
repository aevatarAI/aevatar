using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ProjectionWorkflowActorBindingReaderTests
{
    [Fact]
    public async Task GetAsync_ShouldThrow_WhenActorIdBlank()
    {
        var reader = CreateReader();

        var act = async () => await reader.GetAsync(" ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenReadModelDocumentMissing()
    {
        var reader = CreateReader();

        var result = await reader.GetAsync("actor-1", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldMapRunBinding_FromProjectedDocument()
    {
        var reader = CreateReader(
            getDocumentAsync: (_, _) => Task.FromResult<WorkflowActorBindingDocument?>(new WorkflowActorBindingDocument
            {
                Id = "actor-1",
                ActorKind = WorkflowActorKind.Run,
                DefinitionActorId = "definition-1",
                RunId = "run-1",
                WorkflowName = "direct",
                WorkflowYaml = "yaml",
                InlineWorkflowYamls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["child"] = "yaml-child",
                },
            }));

        var result = await reader.GetAsync("actor-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ActorKind.Should().Be(WorkflowActorKind.Run);
        result.ActorId.Should().Be("actor-1");
        result.DefinitionActorId.Should().Be("definition-1");
        result.RunId.Should().Be("run-1");
        result.WorkflowName.Should().Be("direct");
        result.WorkflowYaml.Should().Be("yaml");
        result.InlineWorkflowYamls.Should().ContainKey("child").WhoseValue.Should().Be("yaml-child");
    }

    [Fact]
    public async Task GetAsync_ShouldUseProjectedDocumentKind_WhenRuntimeWouldInferDifferentKind()
    {
        var reader = CreateReader(
            getDocumentAsync: (_, _) => Task.FromResult<WorkflowActorBindingDocument?>(new WorkflowActorBindingDocument
            {
                Id = "actor-2",
                ActorId = "binding-actor-2",
                ActorKind = WorkflowActorKind.Unsupported,
            }));

        var result = await reader.GetAsync("actor-2", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ActorKind.Should().Be(WorkflowActorKind.Unsupported);
        result.ActorId.Should().Be("binding-actor-2");
    }

    [Fact]
    public async Task ListByRunIdAsync_ShouldReturnProjectedRunRows_WithoutRuntimeFiltering()
    {
        var reader = CreateReader(
            queryDocumentsAsync: (query, _) =>
            {
                query.Filters.Should().Contain(filter =>
                    filter.FieldPath == nameof(WorkflowActorBindingDocument.RunId) &&
                    filter.Operator == ProjectionDocumentFilterOperator.Eq);
                query.Filters.Should().Contain(filter =>
                    filter.FieldPath == nameof(WorkflowActorBindingDocument.ActorKindValue) &&
                    filter.Operator == ProjectionDocumentFilterOperator.Eq);

                return Task.FromResult(new ProjectionDocumentQueryResult<WorkflowActorBindingDocument>
                {
                    Items =
                    [
                        new WorkflowActorBindingDocument
                        {
                            ActorId = "run-actor-1",
                            ActorKind = WorkflowActorKind.Run,
                            DefinitionActorId = "definition-1",
                            RunId = "run-1",
                            WorkflowName = "projected",
                        },
                        new WorkflowActorBindingDocument
                        {
                            ActorId = " ",
                            ActorKind = WorkflowActorKind.Run,
                            DefinitionActorId = "definition-2",
                            RunId = "run-1",
                        },
                    ],
                });
            });

        var result = await reader.ListByRunIdAsync(" run-1 ", take: 500, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].ActorId.Should().Be("run-actor-1");
        result[0].ActorKind.Should().Be(WorkflowActorKind.Run);
        result[0].DefinitionActorId.Should().Be("definition-1");
        result[0].RunId.Should().Be("run-1");
        result[0].WorkflowName.Should().Be("projected");
    }

    [Fact]
    public async Task QueryAsync_ShouldReturnProjectedRunRows_WithoutRuntimeFiltering()
    {
        var reader = CreateReader(
            queryDocumentsAsync: (query, _) =>
            {
                query.Take.Should().Be(200);
                query.Filters.Should().Contain(filter =>
                    filter.FieldPath == nameof(WorkflowActorBindingDocument.ScopeId) &&
                    filter.Operator == ProjectionDocumentFilterOperator.Eq);
                query.Filters.Should().Contain(filter =>
                    filter.FieldPath == nameof(WorkflowActorBindingDocument.DefinitionActorId) &&
                    filter.Operator == ProjectionDocumentFilterOperator.In);

                return Task.FromResult(new ProjectionDocumentQueryResult<WorkflowActorBindingDocument>
                {
                    Items =
                    [
                        new WorkflowActorBindingDocument
                        {
                            ActorId = "run-actor-2",
                            ActorKind = WorkflowActorKind.Run,
                            DefinitionActorId = "definition-2",
                            RunId = "run-2",
                            ScopeId = "scope-1",
                        },
                    ],
                });
            });

        var result = await reader.QueryAsync(
            new WorkflowRunBindingQuery(
                " scope-1 ",
                ["definition-1", "definition-2", "definition-1", " "],
                Take: 500),
            CancellationToken.None);

        result.Should().ContainSingle();
        result[0].ActorId.Should().Be("run-actor-2");
        result[0].ActorKind.Should().Be(WorkflowActorKind.Run);
        result[0].DefinitionActorId.Should().Be("definition-2");
        result[0].RunId.Should().Be("run-2");
        result[0].ScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task GetAsync_ShouldHonorCancellation()
    {
        var reader = CreateReader();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await reader.GetAsync("actor-1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ProjectionWorkflowActorBindingReader CreateReader(
        Func<string, CancellationToken, Task<WorkflowActorBindingDocument?>>? getDocumentAsync = null,
        Func<ProjectionDocumentQuery, CancellationToken, Task<ProjectionDocumentQueryResult<WorkflowActorBindingDocument>>>? queryDocumentsAsync = null)
    {
        var queryAsync = queryDocumentsAsync;
        if (queryAsync == null)
        {
            queryAsync = static (_, _) =>
                Task.FromResult(new ProjectionDocumentQueryResult<WorkflowActorBindingDocument>
                {
                    Items = [],
                });
        }

        return new ProjectionWorkflowActorBindingReader(
            getDocumentAsync ?? ((_, _) => Task.FromResult<WorkflowActorBindingDocument?>(null)),
            queryAsync);
    }
}
