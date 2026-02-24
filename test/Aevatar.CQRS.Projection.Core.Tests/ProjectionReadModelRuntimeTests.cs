using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionReadModelRuntimeTests
{
    [Fact]
    public async Task ProjectionDocumentStoreFanout_ShouldFanoutWritesAndUsePrimaryQueryStore()
    {
        var primaryStore = new NamedDocumentStore("primary");
        var replicaStore = new NamedDocumentStore("replica");
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "replica",
                false,
                _ => replicaStore));
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "primary",
                true,
                _ => primaryStore));

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

        primaryStore.UpsertCount.Should().Be(1);
        replicaStore.UpsertCount.Should().Be(1);

        var fetched = await fanout.GetAsync("id-1");
        fetched.Should().NotBeNull();
        fetched!.Value.Should().Be("v1");
    }

    [Fact]
    public async Task ProjectionDocumentStoreFanout_ShouldReadFromExplicitPrimary_WhenOrderDiffers()
    {
        var primaryStore = new NamedDocumentStore("primary");
        var replicaStore = new NamedDocumentStore("replica");
        primaryStore.Seed("id-1", "from-primary");
        replicaStore.Seed("id-1", "from-replica");

        var services = new ServiceCollection();
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "replica",
                false,
                _ => replicaStore));
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "primary",
                true,
                _ => primaryStore));

        using var serviceProvider = services.BuildServiceProvider();
        var fanout = new ProjectionDocumentStoreFanout<TestReadModel, string>(
            serviceProvider.GetServices<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(),
            serviceProvider);

        var fetched = await fanout.GetAsync("id-1");

        fetched.Should().NotBeNull();
        fetched!.Value.Should().Be("from-primary");
    }

    [Fact]
    public void ProjectionDocumentStoreFanout_WhenMultipleRegistrationsAndNoPrimary_ShouldThrow()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "replica-1",
                false,
                _ => new NamedDocumentStore("replica-1")));
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "replica-2",
                false,
                _ => new NamedDocumentStore("replica-2")));

        using var serviceProvider = services.BuildServiceProvider();
        Action act = () => new ProjectionDocumentStoreFanout<TestReadModel, string>(
            serviceProvider.GetServices<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(),
            serviceProvider);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Exactly one primary document projection store provider must be configured*");
    }

    [Fact]
    public void ProjectionDocumentStoreFanout_WhenMultiplePrimaryRegistrations_ShouldThrow()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "primary-1",
                true,
                _ => new NamedDocumentStore("primary-1")));
        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
                "primary-2",
                true,
                _ => new NamedDocumentStore("primary-2")));

        using var serviceProvider = services.BuildServiceProvider();
        Action act = () => new ProjectionDocumentStoreFanout<TestReadModel, string>(
            serviceProvider.GetServices<IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>>(),
            serviceProvider);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Multiple primary document projection store providers are configured*");
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
