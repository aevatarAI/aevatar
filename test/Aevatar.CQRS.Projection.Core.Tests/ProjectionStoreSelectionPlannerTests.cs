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

        Action act = () => _planner.Build(options, typeof(TestReadModel), new ProjectionStoreRequirements());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*read-model provider is required*");
    }

    [Fact]
    public void Build_WhenRelationProviderMissing_ShouldFallbackToReadModelProvider()
    {
        var options = new FakeOptions
        {
            DocumentProvider = ProjectionProviderNames.Neo4j,
            GraphProvider = " ",
        };

        var plan = _planner.Build(options, typeof(TestReadModel), new ProjectionStoreRequirements(
            requiresGraph: true,
            requiresGraphTraversal: true));

        plan.DocumentSelectionOptions.RequestedProviderName.Should().Be(ProjectionProviderNames.Neo4j);
        plan.GraphSelectionOptions.RequestedProviderName.Should().Be(ProjectionProviderNames.Neo4j);
    }

    [Fact]
    public void Build_ShouldMergeGraphRequirementsWithReadModelAliasAndSchemaRequirements()
    {
        var options = new FakeOptions
        {
            DocumentProvider = ProjectionProviderNames.Neo4j,
        };
        var graphRequirements = new ProjectionStoreRequirements(
            requiresGraph: true,
            requiresGraphTraversal: true,
            requiresAliases: false,
            requiresSchemaValidation: false);

        var plan = _planner.Build(options, typeof(TestGraphReadModel), graphRequirements);

        plan.GraphRequirements.RequiresGraph.Should().BeTrue();
        plan.GraphRequirements.RequiresGraphTraversal.Should().BeTrue();
        plan.GraphRequirements.RequiresAliases.Should().BeFalse();
        plan.GraphRequirements.RequiresSchemaValidation.Should().BeFalse();
    }

    [Fact]
    public void Build_WhenStateOnlyModeConfigured_ShouldThrow()
    {
        var options = new FakeOptions
        {
            DocumentProvider = ProjectionProviderNames.InMemory,
            StoreMode = ProjectionStoreMode.StateOnly,
        };

        Action act = () => _planner.Build(options, typeof(TestReadModel), new ProjectionStoreRequirements());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not support*StateOnly*");
    }

    private sealed class FakeOptions : IProjectionStoreSelectionRuntimeOptions
    {
        public string DocumentProvider { get; set; } = ProjectionProviderNames.InMemory;

        public string GraphProvider { get; set; } = "";

        public bool FailOnUnsupportedCapabilities { get; set; } = true;

        public ProjectionStoreMode StoreMode { get; set; } = ProjectionStoreMode.Custom;
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
