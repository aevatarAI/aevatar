using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using Aevatar.Foundation.Abstractions.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansRuntimeServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAevatarFoundationRuntimeOrleans_ServiceCollection_WhenPersistenceBackendIsUnsupported_ShouldThrow()
    {
        var services = new ServiceCollection();

        var act = () => services.AddAevatarFoundationRuntimeOrleans(options =>
        {
            options.PersistenceBackend = "MongoDB";
        });

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Unsupported Orleans persistence backend*");
    }

    [Fact]
    public void AddAevatarFoundationRuntimeOrleans_ServiceCollection_WhenPersistenceBackendIsGarnetWithoutConnectionString_ShouldThrow()
    {
        var services = new ServiceCollection();

        var act = () => services.AddAevatarFoundationRuntimeOrleans(options =>
        {
            options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendGarnet;
            options.GarnetConnectionString = " ";
            options.GarnetEventStoreKeyPrefix = "aevatar:eventstore";
        });

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Garnet connection string is required*");
    }

    [Fact]
    public void AddAevatarFoundationRuntimeOrleans_ServiceCollection_WhenPersistenceBackendIsGarnetWithoutKeyPrefix_ShouldThrow()
    {
        var services = new ServiceCollection();

        var act = () => services.AddAevatarFoundationRuntimeOrleans(options =>
        {
            options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendGarnet;
            options.GarnetConnectionString = "garnet.internal:6379,abortConnect=false";
            options.GarnetEventStoreKeyPrefix = " ";
        });

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Garnet EventStore key prefix is required*");
    }

    [Fact]
    public void AddAevatarFoundationRuntimeOrleans_ServiceCollection_WhenPersistenceBackendIsGarnet_ShouldRegisterConfiguredOptions()
    {
        var services = new ServiceCollection();

        services.AddAevatarFoundationRuntimeOrleans(options =>
        {
            options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendGarnet;
            options.GarnetConnectionString = "garnet.internal:6379,abortConnect=false";
            options.GarnetEventStoreKeyPrefix = "aevatar:eventstore";
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AevatarOrleansRuntimeOptions>();
        options.PersistenceBackend.Should().Be(AevatarOrleansRuntimeOptions.PersistenceBackendGarnet);
        options.GarnetConnectionString.Should().Be("garnet.internal:6379,abortConnect=false");
        options.GarnetEventStoreKeyPrefix.Should().Be("aevatar:eventstore");

        var descriptor = services.LastOrDefault(x => x.ServiceType == typeof(IEventStore));
        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(typeof(GarnetEventStore));
    }

    [Fact]
    public void AddAevatarFoundationRuntimeOrleans_ServiceCollection_WhenPersistenceBackendIsInMemory_ShouldKeepInMemoryEventStore()
    {
        var services = new ServiceCollection();

        services.AddAevatarFoundationRuntimeOrleans(options =>
        {
            options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
        });

        var descriptor = services.LastOrDefault(x => x.ServiceType == typeof(IEventStore));
        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(typeof(InMemoryEventStore));
    }

    [Fact]
    public void AddAevatarFoundationRuntimeOrleans_ServiceCollection_ShouldRegisterRuntimeActorStateStoreAsOpenGenericIStateStore()
    {
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();

        var descriptor = services.LastOrDefault(x => x.ServiceType == typeof(IStateStore<>));
        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(typeof(RuntimeActorGrainStateStore<>));
    }

    [Fact]
    public void AddAevatarFoundationRuntimeOrleans_SiloBuilder_WhenPersistenceBackendIsUnsupported_ShouldThrow()
    {
        var act = () =>
        {
            using var host = new HostBuilder()
                .UseOrleans(siloBuilder => siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.PersistenceBackend = "MongoDB";
                }))
                .Build();
        };

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Unsupported Orleans persistence backend*");
    }

    [Fact]
    public void AddAevatarFoundationRuntimeOrleans_SiloBuilder_WhenPersistenceBackendIsGarnetWithoutConnectionString_ShouldThrow()
    {
        var act = () =>
        {
            using var host = new HostBuilder()
                .UseOrleans(siloBuilder => siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendGarnet;
                    options.GarnetConnectionString = " ";
                    options.GarnetEventStoreKeyPrefix = "aevatar:eventstore";
                }))
                .Build();
        };

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Garnet connection string is required*");
    }

    [Fact]
    public void AddAevatarFoundationRuntimeOrleans_SiloBuilder_WhenPersistenceBackendIsGarnetWithoutKeyPrefix_ShouldThrow()
    {
        var act = () =>
        {
            using var host = new HostBuilder()
                .UseOrleans(siloBuilder => siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendGarnet;
                    options.GarnetConnectionString = "localhost:6379";
                    options.GarnetEventStoreKeyPrefix = " ";
                }))
                .Build();
        };

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Garnet EventStore key prefix is required*");
    }

    [Fact]
    public void AddAevatarFoundationRuntimeOrleans_SiloBuilder_WhenPersistenceBackendIsGarnet_ShouldNotThrow()
    {
        var act = () =>
        {
            using var host = new HostBuilder()
                .UseOrleans(siloBuilder => siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendGarnet;
                    options.GarnetConnectionString = "localhost:6379";
                    options.GarnetEventStoreKeyPrefix = "aevatar:eventstore";
                }))
                .Build();
        };

        act.Should().NotThrow();
    }
}
