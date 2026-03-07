using Aevatar.Bootstrap.Hosting;
using Aevatar.Foundation.Abstractions.Connectors;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class AevatarDefaultHostOptionsTests
{
    [Fact]
    public void AddAevatarDefaultHost_Default_ShouldRegisterConnectorCatalog()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddAevatarDefaultHost();
        using var provider = builder.Services.BuildServiceProvider();

        provider.GetRequiredService<IConnectorCatalog>().Should().NotBeNull();
    }

    [Fact]
    public void AddAevatarDefaultHost_ShouldPreserveConfiguredOptions()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddAevatarDefaultHost(
            configureHost: options => options.EnableWebSockets = true);

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<AevatarDefaultHostOptions>().EnableWebSockets.Should().BeTrue();
    }
}
