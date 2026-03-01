using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Infrastructure;
using Aevatar.DynamicRuntime.Projection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.DynamicRuntime.Application.Tests;

public sealed class DynamicRuntimeProjectionRegistrationTests
{
    [Fact]
    public void AddDynamicRuntimeProjection_ShouldOverrideReadStoreRegistration()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:InMemory:Enabled"] = "true",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
            })
            .Build();

        services.AddDynamicRuntimeProjection(configuration);
        services.AddDynamicRuntime();

        using var provider = services.BuildServiceProvider();
        var readStore = provider.GetRequiredService<IDynamicRuntimeReadStore>();

        readStore.GetType().Name.Should().Be("ProjectionBackedDynamicRuntimeReadStore");
    }
}
