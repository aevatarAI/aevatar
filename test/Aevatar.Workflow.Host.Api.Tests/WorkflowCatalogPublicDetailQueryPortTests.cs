using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Workflows;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowCatalogPublicDetailQueryPortTests
{
    [Fact]
    public async Task GetPublicWorkflowDetailAsync_ShouldHideNonLibraryReadModels()
    {
        var updatedAt = DateTimeOffset.Parse("2026-03-17T12:00:00+00:00");
        var hiddenReader = new SingleDocumentReader(BuildCatalogDocument("hidden", updatedAt, showInLibrary: false));
        var hiddenPort = new WorkflowCatalogReadModelQueryPort(hiddenReader, new WorkflowCatalogReadModelMapper());
        var publicReader = new SingleDocumentReader(BuildCatalogDocument("visible", updatedAt, sortOrder: 3));
        var publicPort = new WorkflowCatalogReadModelQueryPort(publicReader, new WorkflowCatalogReadModelMapper());

        var hiddenDetail = await hiddenPort.GetPublicWorkflowDetailAsync("hidden");
        var publicDetail = await publicPort.GetPublicWorkflowDetailAsync(" visible ");

        hiddenDetail.Should().BeNull();
        publicDetail.Should().NotBeNull();
        publicDetail!.Catalog.Name.Should().Be("visible");
        publicDetail.Catalog.StepCount.Should().Be(1);
        publicDetail.Catalog.RequiredConnectors.Should().Equal("aevatar_cli");
        hiddenReader.GetCalls.Should().Be(1);
        publicReader.GetCalls.Should().Be(1);
    }

    private static WorkflowCatalogCurrentStateDocument BuildCatalogDocument(
        string workflowName,
        DateTimeOffset updatedAt,
        int sortOrder = 1,
        bool showInLibrary = true) =>
        new()
        {
            Id = workflowName,
            ActorId = $"workflow-definition:{workflowName}",
            WorkflowName = workflowName,
            WorkflowYaml = $"name: {workflowName}",
            Description = "Workflow description",
            Category = "deterministic",
            Group = "starter-workflows",
            GroupLabel = "Starter Workflows",
            SortOrder = sortOrder,
            Source = "repo",
            SourceLabel = "Starter",
            ShowInLibrary = showInLibrary,
            StateVersion = 10 + sortOrder,
            LastEventId = $"evt-{sortOrder}",
            UpdatedAt = updatedAt,
            Primitives = ["assign"],
            Roles =
            [
                new()
                {
                    Id = "operator",
                    Name = "Operator",
                    SystemPrompt = "Operate.",
                    Provider = "openai",
                    Model = "gpt-test",
                    Temperature = 0.1f,
                    MaxTokens = 512,
                    MaxToolRounds = 2,
                    MaxHistoryMessages = 3,
                    Connectors = ["aevatar_cli"],
                },
            ],
            Steps =
            [
                new()
                {
                    Id = "start",
                    Type = "assign",
                    TargetRole = "operator",
                    Parameters = { ["target"] = "result" },
                },
            ],
            RequiredConnectors = ["aevatar_cli"],
        };

    private sealed class SingleDocumentReader(WorkflowCatalogCurrentStateDocument item)
        : IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string>
    {
        public int GetCalls { get; private set; }

        public Task<WorkflowCatalogCurrentStateDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            _ = key;
            ct.ThrowIfCancellationRequested();
            GetCalls++;
            return Task.FromResult<WorkflowCatalogCurrentStateDocument?>(item);
        }

        public Task<ProjectionDocumentQueryResult<WorkflowCatalogCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
