using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowReadModelSelectionPlannerTests
{
    private readonly WorkflowReadModelSelectionPlanner _planner = new(new ProjectionReadModelBindingResolver());

    [Fact]
    public void Build_WhenProviderIsEmpty_ShouldThrow()
    {
        var options = new WorkflowExecutionProjectionOptions
        {
            ReadModelProvider = "  ",
        };

        Action act = () => _planner.Build(options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ReadModelProvider is required*");
    }

    [Fact]
    public void Build_ShouldTrimConfiguredProviderName()
    {
        var options = new WorkflowExecutionProjectionOptions
        {
            ReadModelProvider = "  Neo4j  ",
        };

        var plan = _planner.Build(options);

        plan.ReadModelSelectionOptions.RequestedProviderName.Should().Be(ProjectionReadModelProviderNames.Neo4j);
        plan.RelationSelectionOptions.RequestedProviderName.Should().Be(ProjectionReadModelProviderNames.Neo4j);
    }

    [Fact]
    public void Build_ShouldResolveBindingsByReadModelFullName()
    {
        var options = new WorkflowExecutionProjectionOptions
        {
            ReadModelProvider = ProjectionReadModelProviderNames.InMemory,
        };
        options.ReadModelBindings[typeof(WorkflowExecutionReport).FullName!] =
            ProjectionReadModelIndexKind.Document.ToString();

        var plan = _planner.Build(options);

        plan.ReadModelRequirements.RequiresIndexing.Should().BeTrue();
        plan.ReadModelRequirements.RequiredIndexKinds.Should().ContainSingle()
            .Which.Should().Be(ProjectionReadModelIndexKind.Document);
    }

    [Fact]
    public void Build_WhenBindingUsesShortTypeName_ShouldThrow()
    {
        var options = new WorkflowExecutionProjectionOptions
        {
            ReadModelProvider = ProjectionReadModelProviderNames.InMemory,
        };
        options.ReadModelBindings[nameof(WorkflowExecutionReport)] =
            ProjectionReadModelIndexKind.Document.ToString();

        Action act = () => _planner.Build(options);

        act.Should().Throw<ProjectionReadModelBindingException>()
            .WithMessage("*must use full type name*");
    }

    [Fact]
    public void Build_WhenRelationProviderConfigured_ShouldUseRelationProvider()
    {
        var options = new WorkflowExecutionProjectionOptions
        {
            ReadModelProvider = ProjectionReadModelProviderNames.Elasticsearch,
            RelationProvider = "  InMemory  ",
        };

        var plan = _planner.Build(options);

        plan.ReadModelSelectionOptions.RequestedProviderName.Should().Be(ProjectionReadModelProviderNames.Elasticsearch);
        plan.RelationSelectionOptions.RequestedProviderName.Should().Be(ProjectionReadModelProviderNames.InMemory);
    }

    [Fact]
    public void Build_WhenStateOnlyModeConfigured_ShouldThrow()
    {
        var options = new WorkflowExecutionProjectionOptions
        {
            ReadModelMode = ProjectionReadModelMode.StateOnly,
        };

        Action act = () => _planner.Build(options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not support*StateOnly*");
    }
}
