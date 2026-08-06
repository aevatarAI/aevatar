using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using Aevatar.Foundation.Abstractions.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Persistence;
using Orleans.Runtime;

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
        });

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Garnet connection string is required*");
    }

    [Fact]
    public void AddAevatarFoundationRuntimeOrleans_ServiceCollection_WhenPersistenceBackendIsGarnet_ShouldRegisterConfiguredOptions()
    {
        var services = new ServiceCollection();

        services.AddAevatarFoundationRuntimeOrleans(options =>
        {
            options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendGarnet;
            options.GarnetConnectionString = "garnet.internal:6379,abortConnect=false";
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AevatarOrleansRuntimeOptions>();
        options.PersistenceBackend.Should().Be(AevatarOrleansRuntimeOptions.PersistenceBackendGarnet);
        options.GarnetConnectionString.Should().Be("garnet.internal:6379,abortConnect=false");

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

        var publicationDescriptor = services.LastOrDefault(
            x => x.ServiceType == typeof(ICommittedStatePublicationStateStore));
        publicationDescriptor.Should().NotBeNull();
        publicationDescriptor!.ImplementationType.Should().Be(
            typeof(RuntimeActorGrainCommittedStatePublicationStateStore));
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
                }))
                .Build();
        };

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Garnet connection string is required*");
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
                }))
                .Build();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void AddAevatarFoundationRuntimeOrleans_SiloBuilder_WhenPersistenceBackendIsGarnet_ShouldIsolateCallbackSchedulerStorage()
    {
        using var host = new HostBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: 11113,
                    gatewayPort: 30013,
                    serviceId: "aevatar-runtime-callback-storage-options-service",
                    clusterId: "aevatar-runtime-callback-storage-options-cluster");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendGarnet;
                    options.GarnetConnectionString = "localhost:6379";
                });
            })
            .Build();
        var options = host.Services.GetRequiredService<IOptionsMonitor<RedisStorageOptions>>();

        var schedulerOptions = options.Get(OrleansRuntimeConstants.RuntimeCallbackSchedulerStorageName);
        var sharedOptions = options.Get(OrleansRuntimeConstants.GrainStateStorageName);

        schedulerOptions.GrainStorageSerializer.Should()
            .BeOfType<RuntimeCallbackSchedulerStateGrainStorageSerializer>();
        sharedOptions.GrainStorageSerializer.Should()
            .NotBeOfType<RuntimeCallbackSchedulerStateGrainStorageSerializer>();
        schedulerOptions.GetStorageKey.Should().NotBeNull();

        var schedulerKey = schedulerOptions
            .GetStorageKey!("aevatar-service", GrainId.Create("runtimecallbackscheduler", "actor-1"))
            .ToString();
        schedulerKey.Should().StartWith($"{OrleansRuntimeConstants.RuntimeCallbackSchedulerStorageName}/");
        schedulerKey.Should().EndWith("/aevatar-service");
    }

    [Fact]
    public void AddAevatarFoundationRuntimeOrleans_SiloBuilder_WhenPersistenceBackendIsInMemory_ShouldIsolateCallbackSchedulerStorage()
    {
        using var host = new HostBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: 11114,
                    gatewayPort: 30014,
                    serviceId: "aevatar-runtime-callback-memory-options-service",
                    clusterId: "aevatar-runtime-callback-memory-options-cluster");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
            })
            .Build();
        var options = host.Services.GetRequiredService<IOptionsMonitor<MemoryGrainStorageOptions>>();

        var schedulerOptions = options.Get(OrleansRuntimeConstants.RuntimeCallbackSchedulerStorageName);
        var sharedOptions = options.Get(OrleansRuntimeConstants.GrainStateStorageName);

        schedulerOptions.GrainStorageSerializer.Should()
            .BeOfType<RuntimeCallbackSchedulerStateGrainStorageSerializer>();
        sharedOptions.GrainStorageSerializer.Should()
            .NotBeOfType<RuntimeCallbackSchedulerStateGrainStorageSerializer>();
    }
}
