using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.Audit.Core.Identity;
using Aevatar.Configuration;
using Aevatar.Mainnet.Host.Api.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Capabilities.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
public sealed class MainnetSettingsEndpointSecurityTests
{
    private const string ProviderName = "security-test-provider";
    private const string RawHostSecret = "RAW_MAINNET_PROVIDER_SECRET_MUST_NOT_LEAK";
    private const string ReplacementSecret = "UNTRUSTED_REPLACEMENT_SECRET";
    private const string CrossScopeHint = "scope-other-tenant";

    [Fact]
    public async Task MainnetHost_ShouldExposeOnlyOwnerUserConfig_AndKeepHostProviderSecretReadOnly()
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
        using var authentication = new EnvironmentVariableScope(
            "AEVATAR_Aevatar__Authentication__Enabled", "false");

        var builder = CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        await using var app = builder.Build();
        app.MapAevatarMainnetHost();

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => NormalizeRoutePattern(endpoint.RoutePattern.RawText))
            .ToArray();

        routePatterns.Should().Contain("/api/user-config/llm");
        routePatterns.Should().Contain("/api/user-config/runtime");
        routePatterns.Should().NotContain(route =>
            route.Equals("/api/settings", StringComparison.OrdinalIgnoreCase) ||
            route.StartsWith("/api/settings/", StringComparison.OrdinalIgnoreCase));

        await app.StartAsync();

        var secretsStore = app.Services.GetRequiredService<IAevatarSecretsStore>();
        secretsStore.Should().BeOfType<EnvironmentSecretsStore>();
        secretsStore.GetApiKey(ProviderName).Should().Be(RawHostSecret);

        var client = app.GetTestClient();
        var legacyResponses = new[]
        {
            await client.GetAsync($"/api/settings?scopeId={CrossScopeHint}"),
            await client.SendAsync(new HttpRequestMessage(HttpMethod.Put,
                $"/api/settings?scopeId={CrossScopeHint}")
            {
                Content = JsonContent(new
                {
                    defaultProviderName = ProviderName,
                    providers = new[]
                    {
                        new
                        {
                            providerName = ProviderName,
                            providerType = "openai",
                            model = "security-test-model",
                            apiKey = ReplacementSecret,
                        },
                    },
                }),
            }),
            await client.SendAsync(new HttpRequestMessage(HttpMethod.Post,
                $"/api/settings/runtime/test?scopeId={CrossScopeHint}")
            {
                Content = JsonContent(new { runtimeBaseUrl = "https://runtime.example.test" }),
            }),
        };

        foreach (var response in legacyResponses)
        {
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
            body.Should().NotContain(RawHostSecret);
            body.Should().NotContain(ReplacementSecret);
            body.Should().NotContain(CrossScopeHint);
        }

        secretsStore.GetApiKey(ProviderName).Should().Be(RawHostSecret);

        var openApiResponse = await client.GetAsync("/api/openapi.json");
        var openApiBody = await openApiResponse.Content.ReadAsStringAsync();
        openApiResponse.StatusCode.Should().Be(HttpStatusCode.OK, openApiBody);
        openApiBody.Should().NotContain(RawHostSecret);
        openApiBody.Should().NotContain(ReplacementSecret);
        openApiBody.Should().NotContain(CrossScopeHint);

        using var openApi = JsonDocument.Parse(openApiBody);
        var paths = openApi.RootElement.GetProperty("paths");
        paths.TryGetProperty("/api/user-config/llm", out _).Should().BeTrue();
        paths.TryGetProperty("/api/user-config/runtime", out _).Should().BeTrue();
        paths.EnumerateObject().Select(static path => path.Name).Should().NotContain(path =>
            path.Equals("/api/settings", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/settings/", StringComparison.OrdinalIgnoreCase));

        await app.StopAsync();
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static string NormalizeRoutePattern(string? routePattern) =>
        $"/{routePattern?.TrimStart('/') ?? string.Empty}";

    private static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActorRuntime:Provider"] = "InMemory",
            ["GAgentService:Demo:Enabled"] = "false",
            [$"{AuditActorIdentityHasherOptions.SectionName}:ActiveKeyId"] = "test-key-1",
            [$"{AuditActorIdentityHasherOptions.SectionName}:Keys:0:KeyId"] = "test-key-1",
            [$"{AuditActorIdentityHasherOptions.SectionName}:Keys:0:Key"] =
                "mainnet settings endpoint security audit identity key",
            ["Projection:Document:Providers:InMemory:Enabled"] = "true",
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
            ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
            ["Projection:Graph:Providers:Neo4j:Enabled"] = "false",
            ["Aevatar:NyxId:Authority"] = "https://nyxid.example.test",
            [$"LLMProviders:Providers:{ProviderName}:ApiKey"] = RawHostSecret,
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
            _path = Path.Combine(Path.GetTempPath(), $"aevatar-mainnet-settings-tests-{Guid.NewGuid():N}");
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
