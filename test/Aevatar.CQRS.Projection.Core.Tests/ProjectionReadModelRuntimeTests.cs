using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionReadModelRuntimeTests
{
    [Fact]
    public async Task ProjectionDocumentStoreFanout_ShouldFanoutWritesAndUseFirstRegisteredQueryStore()
    {
        var queryStore = new NamedDocumentStore("query");
        var replicaStore = new NamedDocumentStore("replica");
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "query",
                _ => queryStore));
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "replica",
                _ => replicaStore));

        using var serviceProvider = services.BuildServiceProvider();
        var fanout = new ProjectionDocumentStoreFanout<TestReadModel, string>(
            serviceProvider.GetServices<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(),
            serviceProvider);

        var model = new TestReadModel
        {
            Id = "id-1",
            Value = "v1",
        };

        await fanout.UpsertAsync(model);

        queryStore.UpsertCount.Should().Be(1);
        replicaStore.UpsertCount.Should().Be(1);

        var fetched = await fanout.GetAsync("id-1");
        fetched.Should().NotBeNull();
        fetched!.Value.Should().Be("v1");
    }

    [Fact]
    public async Task ProjectionDocumentStoreFanout_ShouldReadFromFirstRegistration_WhenOrderDiffers()
    {
        var firstStore = new NamedDocumentStore("first");
        var secondStore = new NamedDocumentStore("second");
        firstStore.Seed("id-1", "from-first");
        secondStore.Seed("id-1", "from-second");

        var services = new ServiceCollection();
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "first",
                _ => firstStore));
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "second",
                _ => secondStore));

        using var serviceProvider = services.BuildServiceProvider();
        var fanout = new ProjectionDocumentStoreFanout<TestReadModel, string>(
            serviceProvider.GetServices<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(),
            serviceProvider);

        var fetched = await fanout.GetAsync("id-1");

        fetched.Should().NotBeNull();
        fetched!.Value.Should().Be("from-first");
    }

    [Fact]
    public void ProjectionDocumentStoreFanout_WhenNoRegistrations_ShouldThrow()
    {
        var services = new ServiceCollection();
        using var serviceProvider = services.BuildServiceProvider();

        Action act = () => new ProjectionDocumentStoreFanout<TestReadModel, string>(
            [],
            serviceProvider);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No document projection store providers are registered*");
    }

    public sealed class TestReadModel
    {
        public string Id { get; set; } = "";

        public string Value { get; set; } = "";
    }

    private sealed class NamedDocumentStore : IDocumentProjectionStore<TestReadModel, string>
    {
        private readonly Dictionary<string, TestReadModel> _models = new(StringComparer.Ordinal);

        public NamedDocumentStore(string providerName)
        {
            ProviderName = providerName;
        }

        public string ProviderName { get; }

        public int UpsertCount { get; private set; }

        public void Seed(string key, string value)
        {
            _models[key] = new TestReadModel
            {
                Id = key,
                Value = value,
            };
        }

        public Task UpsertAsync(TestReadModel readModel, CancellationToken ct = default)
        {
            _models[readModel.Id] = new TestReadModel
            {
                Id = readModel.Id,
                Value = readModel.Value,
            };
            UpsertCount++;
            return Task.CompletedTask;
        }

        public Task MutateAsync(string key, Action<TestReadModel> mutate, CancellationToken ct = default)
        {
            if (!_models.TryGetValue(key, out var existing))
                throw new InvalidOperationException($"Missing key '{key}'.");

            mutate(existing);
            return Task.CompletedTask;
        }

        public Task<TestReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            _models.TryGetValue(key, out var value);
            return Task.FromResult<TestReadModel?>(value);
        }

        public Task<IReadOnlyList<TestReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<TestReadModel>>(_models.Values.Take(take).ToList());
        }
    }
}
