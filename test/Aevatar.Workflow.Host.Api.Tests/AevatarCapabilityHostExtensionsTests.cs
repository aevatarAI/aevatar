using Aevatar.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class AevatarCapabilityHostExtensionsTests
{
    [Fact]
    public void AddAevatarCapability_WhenRegisteredTwiceWithSameMapper_ShouldBeIdempotent()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddAevatarCapability("workflow", ConfigureServices, MapEndpoints);
        builder.AddAevatarCapability("workflow", ConfigureServices, MapEndpoints);

        var registrations = builder.Services
            .Where(x => x.ServiceType == typeof(AevatarCapabilityRegistration))
            .Select(x => x.ImplementationInstance)
            .OfType<AevatarCapabilityRegistration>()
            .ToList();

        registrations.Should().HaveCount(1);
    }

    [Fact]
    public void AddAevatarCapability_WhenRegisteredTwiceWithDifferentMapper_ShouldThrow()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddAevatarCapability("workflow", ConfigureServices, MapEndpoints);

        var act = () => builder.AddAevatarCapability("workflow", ConfigureServices, MapEndpointsAlternative);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*workflow*");
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        _ = services;
        _ = configuration;
    }

    private static void MapEndpoints(IEndpointRouteBuilder app)
    {
        _ = app;
    }

    private static void MapEndpointsAlternative(IEndpointRouteBuilder app)
    {
        _ = app;
    }
}
