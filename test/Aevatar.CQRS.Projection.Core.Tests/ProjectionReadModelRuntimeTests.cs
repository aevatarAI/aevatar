using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionReadModelRuntimeTests
{
    [Fact]
    public void ProviderSelector_WhenFailFastDisabled_ShouldReturnRegistration()
    {
        var selector = new ProjectionDocumentStoreProviderSelector(
            new ProjectionProviderCapabilityValidatorService());
        var registrations = new List<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>
        {
            CreateRegistration(
                "InMemory",
                supportsIndexing: false,
                indexKinds: []),
        };

        var selected = selector.Select(
            registrations,
            new ProjectionStoreSelectionOptions
            {
                RequestedProviderName = "InMemory",
                FailOnUnsupportedCapabilities = false,
            },
            new ProjectionStoreRequirements(
                requiresIndexing: true,
                requiredIndexKinds: [ProjectionIndexKind.Document]));

        selected.ProviderName.Should().Be("InMemory");
    }

    [Fact]
    public void ProviderSelector_WhenMultipleProvidersWithoutRequested_ShouldThrowStructuredException()
    {
        var selector = new ProjectionDocumentStoreProviderSelector(
            new ProjectionProviderCapabilityValidatorService());
        var registrations = new List<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>
        {
            CreateRegistration("InMemory", supportsIndexing: false, indexKinds: []),
            CreateRegistration("Elasticsearch", supportsIndexing: true, indexKinds: [ProjectionIndexKind.Document]),
        };

        Action act = () => selector.Select(
            registrations,
            new ProjectionStoreSelectionOptions(),
            new ProjectionStoreRequirements());

        act.Should().Throw<ProjectionProviderSelectionException>()
            .Where(ex => ex.ReadModelType == typeof(TestReadModel));
    }

    private static IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>> CreateRegistration(
        string providerName,
        bool supportsIndexing,
        IReadOnlyList<ProjectionIndexKind> indexKinds)
    {
        var capabilities = new ProjectionProviderCapabilities(
            providerName,
            supportsIndexing,
            indexKinds);

        return new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
            providerName,
            capabilities,
            _ => new NoopStore());
    }

    public sealed class TestReadModel
    {
        public string Id { get; set; } = "";
    }

    private sealed class NoopStore : IDocumentProjectionStore<TestReadModel, string>
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
