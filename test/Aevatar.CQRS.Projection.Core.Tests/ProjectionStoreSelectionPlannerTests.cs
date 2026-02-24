using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionStoreSelectionPlannerTests
{
    private readonly ProjectionStoreSelectionPlanner _planner =
        new(new ProjectionReadModelBindingResolver());

    [Fact]
    public void Build_WhenReadModelProviderIsEmpty_ShouldThrow()
    {
        var options = new FakeOptions
        {
            ReadModelProvider = " ",
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
            ReadModelProvider = ProjectionReadModelProviderNames.Neo4j,
            RelationProvider = " ",
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
            ReadModelProvider = ProjectionReadModelProviderNames.Neo4j,
        };
        options.ReadModelBindings[typeof(TestReadModel).FullName!] = ProjectionReadModelIndexKind.Graph.ToString();
        var relationRequirements = new ProjectionReadModelRequirements(
            requiresRelations: true,
            requiresRelationTraversal: true,
            requiresAliases: false,
            requiresSchemaValidation: false);

        var plan = _planner.Build(options, typeof(TestReadModel), relationRequirements);

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
            ReadModelProvider = ProjectionReadModelProviderNames.InMemory,
            ReadModelMode = ProjectionReadModelMode.StateOnly,
        };

        Action act = () => _planner.Build(options, typeof(TestReadModel), new ProjectionReadModelRequirements());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not support*StateOnly*");
    }

    private sealed class FakeOptions : IProjectionStoreSelectionRuntimeOptions
    {
        private readonly Dictionary<string, string> _bindings = new(StringComparer.OrdinalIgnoreCase);

        public string ReadModelProvider { get; set; } = ProjectionReadModelProviderNames.InMemory;

        public string RelationProvider { get; set; } = "";

        public bool FailOnUnsupportedCapabilities { get; set; } = true;

        public ProjectionReadModelMode ReadModelMode { get; set; } = ProjectionReadModelMode.CustomReadModel;

        public Dictionary<string, string> ReadModelBindings => _bindings;

        IReadOnlyDictionary<string, string> IProjectionStoreSelectionRuntimeOptions.ReadModelBindings => _bindings;
    }

    private sealed class TestReadModel;
}
