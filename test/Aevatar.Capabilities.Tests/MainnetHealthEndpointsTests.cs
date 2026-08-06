using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Core.DependencyInjection;
using Aevatar.Audit.Hosting;
using Aevatar.Bootstrap.Hosting;
using Aevatar.Configuration;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgents.Scheduled;
using Aevatar.Mainnet.Host.Api.Hosting;
using Aevatar.Mainnet.Host.Api.Responses;
using Aevatar.Studio.Hosting;
using Aevatar.Workflow.Extensions.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using System.Net;
using System.Text.Json;

namespace Aevatar.Capabilities.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
public sealed class MainnetHealthEndpointsTests
{
    private const string AuditIdentityTestKeyBase64 = "YXVkaXQgaWRlbnRpdHkga2V5IG1hdGVyaWFsIGZvciB0ZXN0cw==";

    [Fact]
    public async Task MainnetHost_ShouldExposeHealthEndpoints_AndDocumentThemInOpenApi()
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

        builder.AddAevatarDefaultHost(options =>
        {
            options.ServiceName = "Aevatar.Mainnet.Host.Api";
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });
        builder.AddMainnetDistributedOrleansHost();
        builder.AddAevatarPlatform(options =>
        {
            options.EnableMakerExtensions = true;
        });
        builder.Services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://nyx.example.com";
        });
        builder.Services.AddSingleton<IResponsesWebSubstituteBackend, ResponsesWebSubstituteBackendAdapter>();
        builder.Services.AddSingleton<ResponsesWebSubstituteToolExecutionService>();
        builder.Services.AddSingleton<IAuditTrailQueryPort, ReadyAuditTrailQueryPort>();
        builder.Services.AddAuditTrailCore(builder.Configuration);
        builder.AddGAgentServiceCapabilityBundle();
        builder.Services.AddMainnetAgentProjectionDocumentStores(builder.Configuration);
        builder.Services.AddSingleton(Substitute.For<IScheduledAgentCredentialLifecycle>());
        builder.Services.AddScheduledAgents(builder.Configuration);
        builder.AddStudioCapability();
        builder.AddAuditTrailCapabilityBundle();

        await using var app = builder.Build();
        app.UseAevatarDefaultHost();
        await app.StartAsync();

        var client = app.GetTestClient();

        var liveResponse = await client.GetAsync("/health/live");
        liveResponse.EnsureSuccessStatusCode();
        using var livePayload = JsonDocument.Parse(await liveResponse.Content.ReadAsStringAsync());
        livePayload.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        livePayload.RootElement.GetProperty("status").GetString().Should().Be("alive");

        var readinessResponse = await client.GetAsync("/health/ready");
        var readinessBody = await readinessResponse.Content.ReadAsStringAsync();
        readinessResponse.StatusCode.Should().Be(HttpStatusCode.OK, readinessBody);
        using var readinessPayload = JsonDocument.Parse(readinessBody);
        readinessPayload.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        readinessPayload.RootElement.GetProperty("service").GetString().Should().Be("Aevatar.Mainnet.Host.Api");
        var readinessComponents = readinessPayload.RootElement
            .GetProperty("components")
            .EnumerateArray()
            .Select(static component => component.GetProperty("name").GetString())
            .ToArray();
        readinessComponents.Should().Contain(["workflow-bundle", "gagent-service", "studio", "audit-trail"]);
        // Security lockdown: the scripting capability must never be composed into the mainnet host.
        readinessComponents.Should().NotContain("scripting-bundle");
        var studioReadinessComponent = readinessPayload.RootElement
            .GetProperty("components")
            .EnumerateArray()
            .Single(static component => component.GetProperty("name").GetString() == "studio");
        studioReadinessComponent.GetProperty("status").GetString().Should().Be("healthy");
        studioReadinessComponent.GetProperty("message").GetString().Should().Be("Required routes are mapped.");

        var apiHealthResponse = await client.GetAsync("/api/health");
        var apiHealthBody = await apiHealthResponse.Content.ReadAsStringAsync();
        apiHealthResponse.StatusCode.Should().Be(HttpStatusCode.OK, apiHealthBody);
        using var apiHealthPayload = JsonDocument.Parse(apiHealthBody);
        apiHealthPayload.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        apiHealthPayload.RootElement.GetProperty("status").GetString().Should().Be("ready");

        var openApiResponse = await client.GetAsync("/api/openapi.json");
        var openApiBody = await openApiResponse.Content.ReadAsStringAsync();
        openApiResponse.StatusCode.Should().Be(HttpStatusCode.OK, openApiBody);
        using var openApiDocument = JsonDocument.Parse(openApiBody);
        var paths = openApiDocument.RootElement.GetProperty("paths");
        paths.TryGetProperty("/health/live", out _).Should().BeTrue();
        paths.TryGetProperty("/health/ready", out _).Should().BeTrue();
        paths.TryGetProperty("/api/health", out _).Should().BeTrue();

        paths.GetProperty("/").GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content")
            .TryGetProperty("application/json", out _)
            .Should()
            .BeTrue();
        paths.GetProperty("/api/health").GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content")
            .TryGetProperty("application/json", out _)
            .Should()
            .BeTrue();
        paths.GetProperty("/api/app/context").GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content")
            .TryGetProperty("application/json", out _)
            .Should()
            .BeTrue();
        paths.GetProperty("/api/auth/me").GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content")
            .TryGetProperty("application/json", out _)
            .Should()
            .BeTrue();

        var preflightPath =
            "/api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/preflight";
        paths.TryGetProperty(preflightPath, out var preflightOperations).Should().BeTrue();
        preflightOperations.TryGetProperty("post", out _).Should().BeTrue();

        await app.StopAsync();
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
            ["Audit:ActorIdentityHasher:ActiveKeyId"] = "key-1",
            ["Audit:ActorIdentityHasher:Keys:0:KeyId"] = "key-1",
            ["Audit:ActorIdentityHasher:Keys:0:KeyBase64"] = AuditIdentityTestKeyBase64,
        });
        return builder;
    }

    private sealed class ReadyAuditTrailQueryPort : IAuditTrailQueryPort
    {
        public Task<AuditTrailPage> QueryAsync(
            AuditTrailQuery query,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AuditTrailPage(
                [],
                null,
                DateTimeOffset.UnixEpoch,
                AuditQueryCoverage.Create(
                    query,
                    truncated: false,
                    ingestionWatermark: null,
                    completeThrough: null,
                    schemaCompatibility: AuditSchemaCompatibility.Current)));
        }
    }

    private sealed class TemporaryAevatarHomeScope : IDisposable
    {
        private readonly string? _previous;
        private readonly string _path;

        public TemporaryAevatarHomeScope()
        {
            _previous = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
            _path = Path.Combine(Path.GetTempPath(), $"aevatar-mainnet-health-tests-{Guid.NewGuid():N}");
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
