using Aevatar.Hosting;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Application.Queries;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core.Artifacts;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Hosting.CapabilityApi;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Hosting.Tests;

public class ScriptCapabilityHostExtensionsTests
{
    [Fact]
    public void AddScriptingCapabilityBundle_ShouldRegisterCapability()
    {
        Action act = () => ScriptCapabilityHostBuilderExtensions.AddScriptingCapabilityBundle(null!);
        act.Should().Throw<ArgumentNullException>();

        var builder = WebApplication.CreateBuilder();
        var returned = builder.AddScriptingCapabilityBundle();

        returned.Should().BeSameAs(builder);
        var registrations = builder.Services
            .Where(x => x.ServiceType == typeof(AevatarCapabilityRegistration))
            .Select(x => x.ImplementationInstance)
            .OfType<AevatarCapabilityRegistration>()
            .ToList();
        registrations.Should().ContainSingle(x => x.Name == "scripting-bundle");
    }

    [Fact]
    public void AddScriptCapability_ShouldResolveBehaviorAndReadModelServices()
    {
        var services = new ServiceCollection();

        services.AddAevatarRuntime();
        services.AddScriptCapability();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IScriptBehaviorCompiler>().Should().NotBeNull();
        provider.GetRequiredService<IScriptBehaviorArtifactResolver>().Should().NotBeNull();
        provider.GetRequiredService<IScriptBehaviorDispatcher>().Should().NotBeNull();
        provider.GetRequiredService<IScriptBehaviorRuntimeCapabilityFactory>().Should().NotBeNull();
        provider.GetRequiredService<IScriptExecutionProjectionPort>().Should().NotBeNull();
        provider.GetRequiredService<IScriptReadModelQueryPort>().Should().NotBeNull();
        provider.GetRequiredService<IScriptReadModelQueryApplicationService>().Should().NotBeNull();
        provider.GetRequiredService<IScriptEvolutionApplicationService>().Should().NotBeNull();
    }

    [Fact]
    public void AddScriptingCapabilityBundle_ShouldMapEvolutionAndReadModelEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddScriptingCapabilityBundle();

        var app = builder.Build();
        app.MapAevatarCapabilities();

        var routeBuilder = (IEndpointRouteBuilder)app;
        var routeEndpoints = routeBuilder.DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => NormalizeRoute(x.RoutePattern.RawText))
            .ToList();

        routeEndpoints.Should().Contain("/api/scripts/evolutions/proposals");
        routeEndpoints.Should().Contain("/api/scripts/runtimes");
        routeEndpoints.Should().Contain("/api/scripts/runtimes/{actorId}/readmodel");
        routeEndpoints.Should().Contain("/api/scripts/runtimes/{actorId}/queries");
    }

    private static string NormalizeRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route) || string.Equals(route, "/", StringComparison.Ordinal))
            return route ?? string.Empty;

        return route.TrimEnd('/');
    }
}
