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
    public async Task GetAsync_ShouldReturnNull_WhenActorMissing()
    {
        var reader = CreateReader(existsAsync: _ => Task.FromResult(false));

        var result = await reader.GetAsync("missing", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnUnsupportedBinding_WhenActorIsNotWorkflowCapable()
    {
        var reader = CreateReader();

        var result = await reader.GetAsync("actor-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ActorKind.Should().Be(WorkflowActorKind.Unsupported);
        result.ActorId.Should().Be("actor-1");
        result.WorkflowName.Should().BeEmpty();
        result.WorkflowYaml.Should().BeEmpty();
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
            }),
            isExpectedAsync: static (actorId, expectedType, _) => Task.FromResult(
                actorId == "actor-1" && expectedType == typeof(Aevatar.Workflow.Core.WorkflowRunGAgent)));

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
    public async Task GetAsync_ShouldUseVerifierKind_WhenProjectedDocumentDoesNotDeclareKind()
    {
        var reader = CreateReader(
            getDocumentAsync: (_, _) => Task.FromResult<WorkflowActorBindingDocument?>(new WorkflowActorBindingDocument
            {
                Id = "actor-2",
                ActorId = "binding-actor-2",
                ActorKind = WorkflowActorKind.Unsupported,
            }),
            isExpectedAsync: static (actorId, expectedType, _) => Task.FromResult(
                actorId == "actor-2" && expectedType == typeof(Aevatar.Workflow.Core.WorkflowGAgent)));

        var result = await reader.GetAsync("actor-2", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ActorKind.Should().Be(WorkflowActorKind.Definition);
        result.ActorId.Should().Be("binding-actor-2");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnUnboundDefinitionBinding_WhenProjectionHasNoDocument()
    {
        var reader = CreateReader(
            getDocumentAsync: (_, _) => Task.FromResult<WorkflowActorBindingDocument?>(null),
            isExpectedAsync: static (actorId, expectedType, _) => Task.FromResult(
                actorId == "actor-3" && expectedType == typeof(Aevatar.Workflow.Core.WorkflowGAgent)));

        var result = await reader.GetAsync("actor-3", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ActorKind.Should().Be(WorkflowActorKind.Definition);
        result.ActorId.Should().Be("actor-3");
        result.DefinitionActorId.Should().Be("actor-3");
        result.WorkflowYaml.Should().BeEmpty();
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
        Func<ProjectionDocumentQuery, CancellationToken, Task<ProjectionDocumentQueryResult<WorkflowActorBindingDocument>>>? queryDocumentsAsync = null,
        Func<string, Task<bool>>? existsAsync = null,
        Func<string, Type, CancellationToken, Task<bool>>? isExpectedAsync = null)
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
            queryAsync,
            existsAsync ?? (_ => Task.FromResult(true)),
            isExpectedAsync ?? ((_, _, _) => Task.FromResult(false)));
    }
}
