using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionReadModelRuntimeTests
{
    [Fact]
    public void BindingResolver_ShouldResolveRequirement_ByReadModelName()
    {
        var resolver = new ProjectionReadModelBindingResolver();
        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(TestReadModel)] = ProjectionReadModelIndexKind.Graph.ToString(),
        };

        var requirements = resolver.Resolve(bindings, typeof(TestReadModel));

        requirements.RequiresIndexing.Should().BeTrue();
        requirements.RequiredIndexKinds.Should().ContainSingle()
            .Which.Should().Be(ProjectionReadModelIndexKind.Graph);
    }

    [Fact]
    public void ProviderSelector_WhenFailFastDisabled_ShouldReturnRegistration()
    {
        var selector = new ProjectionReadModelProviderSelector(
            new ProjectionReadModelCapabilityValidatorService());
        var registrations = new List<IProjectionReadModelStoreRegistration<TestReadModel, string>>
        {
            CreateRegistration(
                "InMemory",
                supportsIndexing: false,
                indexKinds: []),
        };

        var selected = selector.Select(
            registrations,
            new ProjectionReadModelStoreSelectionOptions
            {
                RequestedProviderName = "InMemory",
                FailOnUnsupportedCapabilities = false,
            },
            new ProjectionReadModelRequirements(
                requiresIndexing: true,
                requiredIndexKinds: [ProjectionReadModelIndexKind.Document]));

        selected.ProviderName.Should().Be("InMemory");
    }

    [Fact]
    public void ProviderSelector_WhenMultipleProvidersWithoutRequested_ShouldThrowStructuredException()
    {
        var selector = new ProjectionReadModelProviderSelector(
            new ProjectionReadModelCapabilityValidatorService());
        var registrations = new List<IProjectionReadModelStoreRegistration<TestReadModel, string>>
        {
            CreateRegistration("InMemory", supportsIndexing: false, indexKinds: []),
            CreateRegistration("Elasticsearch", supportsIndexing: true, indexKinds: [ProjectionReadModelIndexKind.Document]),
        };

        Action act = () => selector.Select(
            registrations,
            new ProjectionReadModelStoreSelectionOptions(),
            new ProjectionReadModelRequirements());

        act.Should().Throw<ProjectionProviderSelectionException>()
            .Where(ex => ex.ReadModelType == typeof(TestReadModel));
    }

    private static IProjectionReadModelStoreRegistration<TestReadModel, string> CreateRegistration(
        string providerName,
        bool supportsIndexing,
        IReadOnlyList<ProjectionReadModelIndexKind> indexKinds)
    {
        var capabilities = new ProjectionReadModelProviderCapabilities(
            providerName,
            supportsIndexing,
            indexKinds);

        return new DelegateProjectionReadModelStoreRegistration<TestReadModel, string>(
            providerName,
            capabilities,
            _ => new NoopStore());
    }

    public sealed class TestReadModel
    {
        public string Id { get; set; } = "";
    }

    private sealed class NoopStore : IProjectionReadModelStore<TestReadModel, string>
    {
        public Task UpsertAsync(TestReadModel readModel, CancellationToken ct = default)
        {
            _ = readModel;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task MutateAsync(string key, Action<TestReadModel> mutate, CancellationToken ct = default)
        {
            _ = key;
            _ = mutate;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task<TestReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            _ = key;
            _ = ct;
            return Task.FromResult<TestReadModel?>(null);
        }

        public Task<IReadOnlyList<TestReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
        {
            _ = take;
            _ = ct;
            return Task.FromResult<IReadOnlyList<TestReadModel>>([]);
        }
    }
}
