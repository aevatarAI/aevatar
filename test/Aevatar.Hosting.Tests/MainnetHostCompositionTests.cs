using Aevatar.Configuration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Mainnet.Host.Api.Hosting;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Hosting.Tests;

public sealed class MainnetHostCompositionTests
{
    [Fact]
    public async Task AddAevatarMainnetHost_WithInMemoryDependencies_ShouldBuildAndStartFullComposition()
    {
        using var home = new TemporaryAevatarHomeScope();
        using var runtimeProvider = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__Provider", "InMemory");
        using var documentProvider = new EnvironmentVariableScope(
            "AEVATAR_Projection__Document__Providers__InMemory__Enabled", "true");
        using var documentElasticsearch = new EnvironmentVariableScope(
            "AEVATAR_Projection__Document__Providers__Elasticsearch__Enabled", "false");
        using var graphProvider = new EnvironmentVariableScope(
            "AEVATAR_Projection__Graph__Providers__InMemory__Enabled", "true");
        using var graphNeo4j = new EnvironmentVariableScope(
            "AEVATAR_Projection__Graph__Providers__Neo4j__Enabled", "false");
        using var projectionEnvironment = new EnvironmentVariableScope(
            "Projection__Policies__Environment", "Development");
        using var denyInMemoryDocument = new EnvironmentVariableScope(
            "Projection__Policies__DenyInMemoryDocumentReadStore", "false");
        using var denyInMemoryGraph = new EnvironmentVariableScope(
            "Projection__Policies__DenyInMemoryGraphFactStore", "false");
        var builder = CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        await using var app = builder.Build();
        app.MapAevatarMainnetHost();
        await app.StartAsync();

        app.Services.GetRequiredService<IServiceRolloutCommandObservationQueryReader>().Should().NotBeNull();
        app.Services.GetRequiredService<IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string>>()
            .Should()
            .NotBeNull();
        app.Services.GetRequiredService<IProjectionDocumentReader<ScriptNativeDocumentReadModel, string>>()
            .Should()
            .NotBeNull();

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .Where(x => x is not null)
            .ToList();

        routePatterns.Should().Contain("/api/webhooks/nyxid-relay/health");
        routePatterns.Should().Contain("/api/channels/registrations");
        routePatterns.Should().Contain("/api/services/");

        await app.StopAsync();
    }

    [Fact]
    public void AddAevatarMainnetHost_ShouldEnableFailFastDiValidation()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });
        builder.Services.AddSingleton<BrokenMainnetService>();

        var act = () => builder.Build();

        act.Should()
            .Throw<Exception>()
            .WithMessage("*MissingMainnetDependency*");
    }

    private static WebApplicationBuilder CreateBuilder()
    {
        var options = new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        };

        var builder = WebApplication.CreateBuilder(options);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActorRuntime:Provider"] = "InMemory",
            ["GAgentService:Demo:Enabled"] = "false",
            ["Projection:Document:Providers:InMemory:Enabled"] = "true",
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
            ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
            ["Projection:Graph:Providers:Neo4j:Enabled"] = "false",
        });
        return builder;
    }

    private sealed class BrokenMainnetService(MissingMainnetDependency dependency)
    {
        public MissingMainnetDependency Dependency { get; } = dependency;
    }

    private sealed class MissingMainnetDependency;

    private sealed class TemporaryAevatarHomeScope : IDisposable
    {
        private readonly string? _previous;
        private readonly string _path;

        public TemporaryAevatarHomeScope()
        {
            _previous = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
            _path = Path.Combine(Path.GetTempPath(), $"aevatar-mainnet-composition-tests-{Guid.NewGuid():N}");
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, _path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, _previous);
            if (Directory.Exists(_path))
                Directory.Delete(_path, recursive: true);
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previous);
        }
    }
}
