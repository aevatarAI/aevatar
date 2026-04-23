using Aevatar.Hosting;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Application.Queries;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Hosting.CapabilityApi;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
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
        provider.GetRequiredService<IScriptReadModelMaterializationCompiler>().Should().NotBeNull();
        provider.GetRequiredService<IScriptNativeDocumentMaterializer>().Should().NotBeNull();
        provider.GetRequiredService<IScriptNativeGraphMaterializer>().Should().NotBeNull();
        provider.GetRequiredService<IScriptExecutionProjectionPort>().Should().NotBeNull();
        provider.GetRequiredService<IScriptReadModelQueryPort>().Should().NotBeNull();
        provider.GetRequiredService<IScriptReadModelQueryApplicationService>().Should().NotBeNull();
        provider.GetRequiredService<IScriptEvolutionApplicationService>().Should().NotBeNull();
    }

    [Fact]
    public void AddScriptCapability_ShouldRegisterElasticsearchDocumentStores_WhenConfigured()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
                ["Projection:Document:Providers:InMemory:Enabled"] = "false",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
            })
            .Build();

        services.AddScriptCapability(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionDocumentReader<ScriptReadModelDocument, string>>()
            .Should().BeOfType<ElasticsearchProjectionDocumentStore<ScriptReadModelDocument, string>>();
        provider.GetRequiredService<IProjectionDocumentReader<ScriptEvolutionReadModel, string>>()
            .Should().BeOfType<ElasticsearchProjectionDocumentStore<ScriptEvolutionReadModel, string>>();
        provider.GetRequiredService<IProjectionDocumentReader<ScriptNativeDocumentReadModel, string>>()
            .Should().BeOfType<ElasticsearchProjectionDocumentStore<ScriptNativeDocumentReadModel, string>>();
        provider.GetRequiredService<IProjectionGraphStore>()
            .Should().BeOfType<InMemoryProjectionGraphStore>();
    }

    [Fact]
    public void AddScriptCapability_ShouldRejectInvalidProjectionProviderFlags()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:InMemory:Enabled"] = "not-a-bool",
            })
            .Build();

        var act = () => services.AddScriptCapability(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid boolean value*");
    }

    [Fact]
    public void AddScriptingProjectionReadModelProviders_ShouldFillMissingReadersWhenPartialRegistrationExists()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddInMemoryDocumentProjectionStore<ScriptReadModelDocument, string>(
            keySelector: static readModel => readModel.Id,
            keyFormatter: static key => key,
            defaultSortSelector: static readModel => readModel.UpdatedAt);

        services.AddScriptingProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionDocumentReader<ScriptEvolutionReadModel, string>>().Should().NotBeNull();
        provider.GetRequiredService<IProjectionDocumentReader<ScriptNativeDocumentReadModel, string>>().Should().NotBeNull();
        services.Count(x => x.ServiceType == typeof(IProjectionDocumentReader<ScriptReadModelDocument, string>)).Should().Be(1);
    }

    [Fact]
    public void AddScriptingProjectionReadModelProviders_ShouldRejectPartialRegistrationFromDifferentProvider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
                ["Projection:Document:Providers:InMemory:Enabled"] = "false",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
                ["Projection:Graph:Providers:Neo4j:Enabled"] = "false",
            })
            .Build();

        services.AddInMemoryDocumentProjectionStore<ScriptReadModelDocument, string>(
            keySelector: static readModel => readModel.Id,
            keyFormatter: static key => key,
            defaultSortSelector: static readModel => readModel.UpdatedAt);

        var act = () => services.AddScriptingProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ScriptReadModelDocument*different provider*");
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
    }

    private static string NormalizeRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route) || string.Equals(route, "/", StringComparison.Ordinal))
            return route ?? string.Empty;

        return route.TrimEnd('/');
    }
}
