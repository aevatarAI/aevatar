using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionStoreSelectionPlannerTests
{
    private readonly ProjectionStoreSelectionPlanner _planner =
        new();

    [Fact]
    public void Build_WhenReadModelProviderIsEmpty_ShouldThrow()
    {
        var options = new FakeOptions
        {
            DocumentProvider = " ",
        };

        Action act = () => _planner.Build(options, typeof(TestReadModel), new ProjectionReadModelRequirements());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*read-model provider is required*");
    }

    [Fact]
    public void Build_WhenRelationProviderMissing_ShouldFallbackToReadModelProvider()
    {
        var options = new FakeOptions
        {
            DocumentProvider = ProjectionReadModelProviderNames.Neo4j,
            GraphProvider = " ",
        };

        var plan = _planner.Build(options, typeof(TestReadModel), new ProjectionReadModelRequirements(
            requiresRelations: true,
            requiresRelationTraversal: true));

        plan.ReadModelSelectionOptions.RequestedProviderName.Should().Be(ProjectionReadModelProviderNames.Neo4j);
        plan.RelationSelectionOptions.RequestedProviderName.Should().Be(ProjectionReadModelProviderNames.Neo4j);
    }

    [Fact]
    public void Build_ShouldMergeRelationRequirementsWithReadModelAliasAndSchemaRequirements()
    {
        var options = new FakeOptions
        {
            DocumentProvider = ProjectionReadModelProviderNames.Neo4j,
        };
        var relationRequirements = new ProjectionReadModelRequirements(
            requiresRelations: true,
            requiresRelationTraversal: true,
            requiresAliases: false,
            requiresSchemaValidation: false);

        var plan = _planner.Build(options, typeof(TestGraphReadModel), relationRequirements);

        plan.RelationRequirements.RequiresRelations.Should().BeTrue();
        plan.RelationRequirements.RequiresRelationTraversal.Should().BeTrue();
        plan.RelationRequirements.RequiresAliases.Should().BeFalse();
        plan.RelationRequirements.RequiresSchemaValidation.Should().BeFalse();
    }

    [Fact]
    public void Build_WhenStateOnlyModeConfigured_ShouldThrow()
    {
        var options = new FakeOptions
        {
            DocumentProvider = ProjectionReadModelProviderNames.InMemory,
            ReadModelMode = ProjectionReadModelMode.StateOnly,
        };

        Action act = () => _planner.Build(options, typeof(TestReadModel), new ProjectionReadModelRequirements());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not support*StateOnly*");
    }

    private sealed class FakeOptions : IProjectionStoreSelectionRuntimeOptions
    {
        public string DocumentProvider { get; set; } = ProjectionReadModelProviderNames.InMemory;

        public string GraphProvider { get; set; } = "";

        public bool FailOnUnsupportedCapabilities { get; set; } = true;

        public ProjectionReadModelMode ReadModelMode { get; set; } = ProjectionReadModelMode.CustomReadModel;
    }

    private sealed class TestReadModel;

    private sealed class TestGraphReadModel : IGraphReadModel
    {
        public string Id => "test";

        public string GraphScope => "test";

        public IReadOnlyList<GraphNodeDescriptor> GraphNodes => [];

        public IReadOnlyList<GraphEdgeDescriptor> GraphEdges => [];
    }
}
