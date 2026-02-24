using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionReadModelRuntimeTests
{
    [Fact]
    public void DocumentProviderSelector_WhenRequestedProviderMatched_ShouldReturnRegistration()
    {
        var selector = new ProjectionDocumentStoreProviderSelector();
        var registrations = new List<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>
        {
            CreateRegistration("InMemory"),
            CreateRegistration("Elasticsearch"),
        };

        var selected = selector.Select(
            registrations,
            new ProjectionDocumentSelectionOptions
            {
                RequestedProviderName = "inmemory",
            });

        selected.ProviderName.Should().Be("InMemory");
    }

    [Fact]
    public void DocumentProviderSelector_WhenMultipleProvidersWithoutRequested_ShouldThrowStructuredException()
    {
        var selector = new ProjectionDocumentStoreProviderSelector();
        var registrations = new List<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>
        {
            CreateRegistration("InMemory"),
            CreateRegistration("Elasticsearch"),
        };

        Action act = () => selector.Select(
            registrations,
            new ProjectionDocumentSelectionOptions());

        act.Should().Throw<ProjectionProviderSelectionException>()
            .Where(ex => ex.ReadModelType == typeof(TestReadModel));
    }

    private static IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>> CreateRegistration(
        string providerName)
    {
        return new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
            providerName,
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
