using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Workflows;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowCatalogCapabilityRegistrationTests
{
    [Fact]
    public async Task AddWorkflowCapability_ShouldResolveWorkflowCatalogPortFromReadModelProjection()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var updatedAt = DateTimeOffset.Parse("2026-03-17T12:00:00+00:00");
        var catalogDocument = new WorkflowCatalogCurrentStateDocument
        {
            Id = "direct",
            ActorId = "workflow-definition:direct",
            WorkflowName = "direct",
            WorkflowYaml = "name: direct",
            Description = "Direct workflow",
            Group = "starter-workflows",
            GroupLabel = "Starter Workflows",
            Source = "builtin",
            SourceLabel = "Built-in",
            ShowInLibrary = true,
            StateVersion = 12,
            LastEventId = "evt-direct",
            UpdatedAt = updatedAt,
            Steps =
            [
                new WorkflowCatalogStepReadModel
                {
                    Id = "llm",
                    Type = "llm_call",
                },
            ],
        };
        services.AddSingleton<IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string>>(
            new SingleWorkflowCatalogDocumentReader(catalogDocument));

        services.AddWorkflowCapability(configuration);

        using var provider = services.BuildServiceProvider();
        var catalogPort = provider.GetRequiredService<IWorkflowCatalogPort>();
        var detail = await catalogPort.GetPublicWorkflowDetailAsync("direct");

        catalogPort.Should().BeOfType<WorkflowCatalogReadModelQueryPort>();
        detail.Should().NotBeNull();
        detail!.Catalog.StepCount.Should().Be(1);
        detail.Catalog.AuthorityStateVersion.Should().Be(12);
        detail.Catalog.ProjectionWatermark.Should().Be(updatedAt);
        detail.Definition.Steps.Should().ContainSingle(step =>
            step.Id == "llm" &&
            step.Type == "llm_call");
    }

    private sealed class SingleWorkflowCatalogDocumentReader(WorkflowCatalogCurrentStateDocument item)
        : IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string>
    {
        public Task<WorkflowCatalogCurrentStateDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WorkflowCatalogCurrentStateDocument?>(
                string.Equals(key, item.Id, StringComparison.Ordinal) ? item : null);
        }

        public Task<ProjectionDocumentQueryResult<WorkflowCatalogCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            _ = query;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectionDocumentQueryResult<WorkflowCatalogCurrentStateDocument>
            {
                Items = [item],
            });
        }
    }
}
