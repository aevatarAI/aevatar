using Aevatar.Bootstrap.Hosting;
using Aevatar.Foundation.Abstractions.Connectors;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public class AevatarDefaultHostExtensionsTests
{
    [Fact]
    public void AddAevatarDefaultHost_ShouldRegisterConnectorCatalog()
    {
        var builder = CreateBuilder();

        builder.AddAevatarDefaultHost();
        using var provider = builder.Services.BuildServiceProvider();

        provider.GetRequiredService<IConnectorCatalog>().Should().NotBeNull();
    }

    [Fact]
    public void AddAevatarDefaultHost_ShouldNotRegisterHostedBootstrapMutation()
    {
        var builder = CreateBuilder();

        builder.AddAevatarDefaultHost();

        builder.Services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService));
    }

    private static WebApplicationBuilder CreateBuilder()
    {
        return WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(AevatarDefaultHostExtensionsTests).Assembly.FullName,
        });
    }
}
