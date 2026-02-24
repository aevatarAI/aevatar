using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionReadModelRuntimeTests
{
    [Fact]
    public void DocumentStoreFactory_WhenRequestedProviderMatched_ShouldCreateRequestedProviderStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "InMemory",
                _ => new NamedDocumentStore("InMemory")));
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "Elasticsearch",
                _ => new NamedDocumentStore("Elasticsearch")));

        using var serviceProvider = services.BuildServiceProvider();
        var factory = new ProjectionDocumentStoreFactory();

        var selected = factory.Create<TestReadModel, string>(serviceProvider, "inmemory");
        var typed = selected.Should().BeOfType<NamedDocumentStore>().Subject;
        typed.ProviderName.Should().Be("InMemory");
    }

    [Fact]
    public void DocumentStoreFactory_WhenMultipleProvidersWithoutRequested_ShouldThrowStructuredException()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "InMemory",
                _ => new NamedDocumentStore("InMemory")));
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "Elasticsearch",
                _ => new NamedDocumentStore("Elasticsearch")));

        using var serviceProvider = services.BuildServiceProvider();
        var factory = new ProjectionDocumentStoreFactory();

        Action act = () => factory.Create<TestReadModel, string>(serviceProvider);

        act.Should().Throw<ProjectionProviderSelectionException>()
            .Where(ex => ex.ReadModelType == typeof(TestReadModel));
    }

    public sealed class TestReadModel
    {
        public string Id { get; set; } = "";
    }

    private sealed class NamedDocumentStore : IDocumentProjectionStore<TestReadModel, string>
    {
        public NamedDocumentStore(string providerName)
        {
            ProviderName = providerName;
        }

        public string ProviderName { get; }

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
