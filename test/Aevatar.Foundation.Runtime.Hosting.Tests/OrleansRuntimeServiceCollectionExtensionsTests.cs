using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
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
}
