using Aevatar.Bootstrap;
using Aevatar.Bootstrap.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public class AevatarDefaultHostExtensionsTests
{
    [Fact]
    public void AddAevatarDefaultHost_ShouldRegisterConnectorBootstrapHostedService_ByDefault()
    {
        var builder = CreateBuilder();

        builder.AddAevatarDefaultHost(
            configureBootstrap: static options => options.EnableMEAIProviders = false);

        builder.Services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(ConnectorBootstrapHostedService));
    }

    [Fact]
    public void AddAevatarDefaultHost_WhenConnectorBootstrapDisabled_ShouldNotRegisterConnectorBootstrapHostedService()
    {
        var builder = CreateBuilder();

        builder.AddAevatarDefaultHost(
            configureBootstrap: static options => options.EnableMEAIProviders = false,
            configureHost: static options => options.EnableConnectorBootstrap = false);

        builder.Services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(ConnectorBootstrapHostedService));
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
