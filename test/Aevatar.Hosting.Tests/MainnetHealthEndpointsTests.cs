using Aevatar.Bootstrap.Hosting;
using Aevatar.Configuration;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Mainnet.Host.Api.Hosting;
using Aevatar.Studio.Hosting;
using Aevatar.Workflow.Extensions.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Text.Json;

namespace Aevatar.Hosting.Tests;

public sealed class MainnetHealthEndpointsTests
{
    [Fact]
    public async Task MainnetHost_ShouldExposeHealthEndpoints_AndDocumentThemInOpenApi()
    {
        using var home = new TemporaryAevatarHomeScope();
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
        builder.AddGAgentServiceCapabilityBundle();
        builder.AddStudioCapability();

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
        readinessPayload.RootElement
            .GetProperty("components")
            .EnumerateArray()
            .Select(static component => component.GetProperty("name").GetString())
            .Should()
            .Contain(["workflow-bundle", "gagent-service", "studio", "scripting-bundle"]);

        var apiHealthResponse = await client.GetAsync("/api/health");
        var apiHealthBody = await apiHealthResponse.Content.ReadAsStringAsync();
        apiHealthResponse.StatusCode.Should().Be(HttpStatusCode.OK, apiHealthBody);
        using var apiHealthPayload = JsonDocument.Parse(apiHealthBody);
        apiHealthPayload.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        apiHealthPayload.RootElement.GetProperty("status").GetString().Should().Be("ready");

        using var openApiDocument = JsonDocument.Parse(await client.GetStringAsync("/api/openapi.json"));
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
        });
        return builder;
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
}
