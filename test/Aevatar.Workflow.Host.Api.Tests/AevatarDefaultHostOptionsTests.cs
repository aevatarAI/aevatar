using Aevatar.Bootstrap.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class AevatarDefaultHostOptionsTests
{
    [Fact]
    public void AddAevatarDefaultHost_Default_ShouldRegisterActorRestoreHostedService()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddAevatarDefaultHost(configureBootstrap: options => options.EnableMEAIProviders = false);

        var hostedServices = builder.Services
            .Where(x => x.ServiceType == typeof(IHostedService))
            .ToList();

        hostedServices.Should().Contain(x => x.ImplementationType == typeof(ActorRestoreHostedService));
    }

    [Fact]
    public void AddAevatarDefaultHost_WhenActorRestoreDisabled_ShouldNotRegisterActorRestoreHostedService()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddAevatarDefaultHost(
            configureBootstrap: options => options.EnableMEAIProviders = false,
            configureHost: options => options.EnableActorRestoreOnStartup = false);

        var hostedServices = builder.Services
            .Where(x => x.ServiceType == typeof(IHostedService))
            .ToList();

        hostedServices.Should().NotContain(x => x.ImplementationType == typeof(ActorRestoreHostedService));
    }
}
