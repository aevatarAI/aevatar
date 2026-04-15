using Aevatar.Bootstrap;
using Aevatar.Bootstrap.Hosting;
using Aevatar.Foundation.VoicePresence.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public class AevatarDefaultHostExtensionsTests
{
    [Fact]
    public void AddAevatarDefaultHost_ShouldRegisterConnectorBootstrapHostedService_ByDefault()
    {
        var builder = CreateBuilder();

        builder.AddAevatarDefaultHost();

        builder.Services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(ConnectorBootstrapHostedService));
    }

    [Fact]
    public void AddAevatarDefaultHost_WhenConnectorBootstrapDisabled_ShouldNotRegisterConnectorBootstrapHostedService()
    {
        var builder = CreateBuilder();

        builder.AddAevatarDefaultHost(
            configureHost: static options => options.EnableConnectorBootstrap = false);

        builder.Services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(ConnectorBootstrapHostedService));
    }

    [Fact]
    public void UseAevatarDefaultHost_WhenVoicePresenceResolverRegistered_ShouldMapVoiceWebSocketRoute()
    {
        var builder = CreateBuilder();
        builder.AddAevatarDefaultHost();
        builder.Services.AddSingleton<IVoicePresenceSessionResolver, NullVoicePresenceSessionResolver>();

        using var app = builder.Build();

        app.UseAevatarDefaultHost();

        GetRoutePatterns(app).Should().Contain("/ws/voice/{actorId}");
    }

    [Fact]
    public void UseAevatarDefaultHost_WhenVoicePresenceResolverMissing_ShouldNotMapVoiceWebSocketRoute()
    {
        var builder = CreateBuilder();
        builder.AddAevatarDefaultHost();

        using var app = builder.Build();

        app.UseAevatarDefaultHost();

        GetRoutePatterns(app).Should().NotContain("/ws/voice/{actorId}");
    }

    private static WebApplicationBuilder CreateBuilder()
    {
        return WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(AevatarDefaultHostExtensionsTests).Assembly.FullName,
        });
    }

    private static IEnumerable<string?> GetRoutePatterns(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText);

    private sealed class NullVoicePresenceSessionResolver : IVoicePresenceSessionResolver
    {
        public Task<VoicePresenceSession?> ResolveAsync(VoicePresenceSessionRequest request, CancellationToken ct = default)
        {
            _ = request;
            _ = ct;
            return Task.FromResult<VoicePresenceSession?>(null);
        }
    }
}
