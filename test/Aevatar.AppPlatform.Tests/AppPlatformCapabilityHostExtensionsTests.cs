using Aevatar.AppPlatform.Hosting;
using Aevatar.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Aevatar.AppPlatform.Tests;

public class AppPlatformCapabilityHostExtensionsTests
{
    [Fact]
    public void AddAppPlatformCapability_ShouldRegisterCapability()
    {
        var builder = WebApplication.CreateBuilder();

        var returned = builder.AddAppPlatformCapability();

        returned.Should().BeSameAs(builder);
        var registrations = builder.Services
            .Where(x => x.ServiceType == typeof(AevatarCapabilityRegistration))
            .Select(x => x.ImplementationInstance)
            .OfType<AevatarCapabilityRegistration>()
            .ToList();

        registrations.Should().ContainSingle(x => x.Name == "app-platform");
    }

    [Fact]
    public void AddAppPlatformCapability_ShouldMapEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddAppPlatformCapability();

        var app = builder.Build();
        app.MapAevatarCapabilities();

        var routeBuilder = (IEndpointRouteBuilder)app;
        var routeEndpoints = routeBuilder.DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => NormalizeRoute(x.RoutePattern.RawText))
            .ToList();

        routeEndpoints.Should().Contain("/api/apps");
        routeEndpoints.Should().Contain("/api/apps/resolve");
        routeEndpoints.Should().Contain("/api/apps/{appId}");
        routeEndpoints.Should().Contain("/api/apps/{appId}/routes");
        routeEndpoints.Should().Contain("/api/apps/{appId}/releases");
    }

    private static string NormalizeRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route) || string.Equals(route, "/", StringComparison.Ordinal))
            return route ?? string.Empty;

        return route.TrimEnd('/');
    }
}
