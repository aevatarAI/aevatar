using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Infrastructure;
using Aevatar.DynamicRuntime.Projection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.DynamicRuntime.Application.Tests;

public sealed class DynamicRuntimeProjectionRegistrationTests
{
    [Fact]
    public void AddDynamicRuntimeProjection_ShouldOverrideReadStoreRegistration()
    {
        var services = new ServiceCollection();

        services.AddDynamicRuntimeProjection();
        services.AddDynamicRuntime();

        using var provider = services.BuildServiceProvider();
        var readStore = provider.GetRequiredService<IDynamicRuntimeReadStore>();

        readStore.GetType().Name.Should().Be("ProjectionBackedDynamicRuntimeReadStore");
    }
}
