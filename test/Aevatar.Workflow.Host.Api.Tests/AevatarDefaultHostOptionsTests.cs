using Aevatar.Bootstrap.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class AevatarDefaultHostOptionsTests
{
    [Fact]
    public void AddAevatarDefaultHost_Default_ShouldRegisterConnectorBootstrapHostedService()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddAevatarDefaultHost();

        var hostedServices = builder.Services
            .Where(x => x.ServiceType == typeof(IHostedService))
            .ToList();

        hostedServices.Should().Contain(x => x.ImplementationType == typeof(ConnectorBootstrapHostedService));
    }

    [Fact]
    public void AddAevatarDefaultHost_WhenConnectorBootstrapDisabled_ShouldNotRegisterConnectorBootstrapHostedService()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddAevatarDefaultHost(
            configureHost: options => options.EnableConnectorBootstrap = false);

        var hostedServices = builder.Services
            .Where(x => x.ServiceType == typeof(IHostedService))
            .ToList();

        hostedServices.Should().NotContain(x => x.ImplementationType == typeof(ConnectorBootstrapHostedService));
    }
}
