using Aevatar.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Hosting.Tests;

public class AevatarCapabilityHostExtensionsTests
{
    [Fact]
    public void AddAevatarCapability_WhenDuplicateNameWithDifferentMapper_ShouldThrow()
    {
        var builder = CreateBuilder();

        builder.AddAevatarCapability(
            "workflow",
            static (_, _) => { },
            static app => app.MapGet("/v1", () => Results.Ok()));

        var act = () => builder.AddAevatarCapability(
            "workflow",
            static (_, _) => { },
            static app => app.MapGet("/v2", () => Results.Ok()));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*already registered with a different endpoint mapper*");
    }

    [Fact]
    public void AddAevatarCapability_WhenDuplicateNameWithSameMapper_ShouldBeIdempotent()
    {
        var builder = CreateBuilder();
        Action<IEndpointRouteBuilder> mapper = static app => app.MapGet("/workflow/health", () => Results.Ok());

        builder.AddAevatarCapability("workflow", static (_, _) => { }, mapper);
        builder.AddAevatarCapability("workflow", static (_, _) => { }, mapper);

        var app = builder.Build();
        app.MapAevatarCapabilities();

        var routeBuilder = (IEndpointRouteBuilder)app;
        var endpoints = routeBuilder.DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(x => string.Equals(x.RoutePattern.RawText, "/workflow/health", StringComparison.Ordinal))
            .ToList();

        endpoints.Should().HaveCount(1);
    }

    [Fact]
    public void MapAevatarCapabilities_WhenRegistrationExists_ShouldMapEndpoint()
    {
        var builder = CreateBuilder();

        builder.AddAevatarCapability(
            "workflow",
            static (_, _) => { },
            static app => app.MapGet("/api/workflows", () => Results.Ok()));

        var app = builder.Build();
        app.MapAevatarCapabilities();

        var routeBuilder = (IEndpointRouteBuilder)app;
        var routeEndpoints = routeBuilder.DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .ToList();

        routeEndpoints.Should().Contain("/api/workflows");
    }

    [Fact]
    public void MapAevatarCapabilities_WhenManualDuplicateRegistrations_ShouldThrow()
    {
        var builder = CreateBuilder();
        builder.Services.AddSingleton(new AevatarCapabilityRegistration
        {
            Name = "workflow",
            MapEndpoints = static _ => { },
        });
        builder.Services.AddSingleton(new AevatarCapabilityRegistration
        {
            Name = "workflow",
            MapEndpoints = static _ => { },
        });

        var app = builder.Build();
        var act = () => app.MapAevatarCapabilities();

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*registered more than once*");
    }

    private static WebApplicationBuilder CreateBuilder()
    {
        var options = new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        };
        return WebApplication.CreateBuilder(options);
    }
}
