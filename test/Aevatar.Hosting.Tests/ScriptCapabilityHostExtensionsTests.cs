using Aevatar.Hosting;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Hosting.CapabilityApi;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.Scripting.Infrastructure.Ports;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Hosting.Tests;

public class ScriptCapabilityHostExtensionsTests
{
    [Fact]
    public void AddScriptCapability_ShouldRegisterCapabilityAndValidateNull()
    {
        Action act = () => ScriptCapabilityHostBuilderExtensions.AddScriptCapability(null!);
        act.Should().Throw<ArgumentNullException>();

        var builder = WebApplication.CreateBuilder();
        var returned = builder.AddScriptCapability();

        returned.Should().BeSameAs(builder);
        var registrations = builder.Services
            .Where(x => x.ServiceType == typeof(AevatarCapabilityRegistration))
            .Select(x => x.ImplementationInstance)
            .OfType<AevatarCapabilityRegistration>()
            .ToList();
        registrations.Should().ContainSingle(x => x.Name == "script");
    }

    [Fact]
    public void AddScriptCapabilityServices_ShouldRegisterCoreScriptServices()
    {
        var services = new ServiceCollection();

        services.AddScriptCapability();

        services.Should().Contain(x =>
            x.ServiceType == typeof(ScriptSandboxPolicy) &&
            x.ImplementationType == typeof(ScriptSandboxPolicy));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IScriptPackageCompiler) &&
            x.ImplementationType == typeof(RoslynScriptPackageCompiler));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IScriptEvolutionApplicationService) &&
            x.ImplementationType == typeof(ScriptEvolutionApplicationService));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IScriptRuntimeCapabilityComposer) &&
            x.ImplementationType == typeof(ScriptRuntimeCapabilityComposer));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IScriptingActorAddressResolver) &&
            x.ImplementationType == typeof(DefaultScriptingActorAddressResolver));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IScriptDefinitionSnapshotPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IScriptEvolutionProposalPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IScriptDefinitionCommandPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IScriptRuntimeCommandPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IScriptCatalogCommandPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IScriptCatalogQueryPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IScriptEvolutionFlowPort) &&
            x.ImplementationType == typeof(RuntimeScriptEvolutionFlowPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IGAgentRuntimePort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IAICapability));
    }

    [Fact]
    public void AddScriptCapability_ShouldMapEvolutionProposalEndpoint()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddScriptCapability();

        var app = builder.Build();
        app.MapAevatarCapabilities();

        var routeBuilder = (IEndpointRouteBuilder)app;
        var routeEndpoints = routeBuilder.DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .ToList();

        routeEndpoints.Should().Contain("/api/scripts/evolutions/proposals");
    }
}
