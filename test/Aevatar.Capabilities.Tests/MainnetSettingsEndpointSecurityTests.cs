using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Audit.Core.Identity;
using Aevatar.Authentication.Abstractions;
using Aevatar.Configuration;
using Aevatar.Mainnet.Host.Api.Hosting;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.Capabilities.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
public sealed class MainnetSettingsEndpointSecurityTests
{
    private const string ProviderName = "security-test-provider";
    private const string RawHostSecret = "RAW_MAINNET_PROVIDER_SECRET_MUST_NOT_LEAK";
    private const string ReplacementSecret = "UNTRUSTED_REPLACEMENT_SECRET";
    private const string CrossScopeHint = "scope-other-tenant";
    private const string OwnerAScope = "scope-owner-alpha";
    private const string OwnerBScope = "scope-owner-beta";
    private const string OwnerAModel = "owner-alpha-model";
    private const string OwnerARuntimeUrl = "https://owner-alpha-runtime.example.test";

    [Fact]
    public async Task MainnetHost_ShouldExposeOnlyOwnerUserConfig_AndKeepHostProviderSecretReadOnly()
    {
        await using var host = await MainnetTestHost.StartAsync();
        var ownerAToken = host.CreateToken("owner-alpha", OwnerAScope);
        var ownerBToken = host.CreateToken("owner-beta", OwnerBScope);
        var platformAdminToken = host.CreateToken("platform-admin", "scope-platform", platformAdmin: true);

        AssertRouteMetadata(host.App);
        await AssertLegacyEndpointCallerMatrixAsync(host.Client, ownerAToken, platformAdminToken);
        await AssertOwnerScopedUserConfigAsync(host, ownerAToken, ownerBToken);
        await AssertOpenApiAsync(host.Client, ownerAToken);

        host.SecretsStore.GetApiKey(ProviderName).Should().Be(RawHostSecret);
        host.UserConfigs.Resources.Should().NotContain(resource => resource.Value == CrossScopeHint);
    }

    private static void AssertRouteMetadata(IEndpointRouteBuilder app)
    {
        var routePatterns = app.DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => NormalizeRoutePattern(endpoint.RoutePattern.RawText))
            .ToArray();

        routePatterns.Should().Contain("/api/user-config/llm");
        routePatterns.Should().Contain("/api/user-config/runtime");
        routePatterns.Should().NotContain(route => IsLegacySettingsPath(route));
    }

    private static async Task AssertLegacyEndpointCallerMatrixAsync(
        HttpClient client,
        string ownerToken,
        string platformAdminToken)
    {
        var callers = new[]
        {
            new LegacyCaller("anonymous", Token: null, ScopeHint: null, HttpStatusCode.Unauthorized),
            new LegacyCaller("ordinary-owner", ownerToken, ScopeHint: null, HttpStatusCode.NotFound),
            new LegacyCaller("cross-scope-hint", ownerToken, CrossScopeHint, HttpStatusCode.NotFound),
            new LegacyCaller("platform-admin", platformAdminToken, ScopeHint: null, HttpStatusCode.NotFound),
        };

        foreach (var caller in callers)
        {
            foreach (var request in CreateLegacyRequests(caller.ScopeHint))
            {
                using (request)
                {
                    if (caller.Token is not null)
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

                    using var response = await client.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    response.StatusCode.Should().Be(caller.ExpectedStatus, $"caller={caller.Name}; body={body}");
                    AssertNoSensitiveValues(body);
                }
            }
        }
    }

    private static async Task AssertOwnerScopedUserConfigAsync(
        MainnetTestHost host,
        string ownerAToken,
        string ownerBToken)
    {
        using var llmWrite = CreateAuthenticatedJsonRequest(
            HttpMethod.Put,
            $"/api/user-config/llm?scopeId={CrossScopeHint}",
            ownerAToken,
            new
            {
                action = "select_gateway",
                gateway = new
                {
                    model = new { kind = "explicit_model", modelId = OwnerAModel },
                },
            });
        using var llmWriteResponse = await host.Client.SendAsync(llmWrite);
        llmWriteResponse.StatusCode.Should().Be(
            HttpStatusCode.Accepted,
            await llmWriteResponse.Content.ReadAsStringAsync());

        using var runtimeWrite = CreateAuthenticatedJsonRequest(
            HttpMethod.Put,
            $"/api/user-config?scopeId={CrossScopeHint}",
            ownerAToken,
            new { runtimeMode = "remote", remoteRuntimeBaseUrl = OwnerARuntimeUrl });
        using var runtimeWriteResponse = await host.Client.SendAsync(runtimeWrite);
        runtimeWriteResponse.StatusCode.Should().Be(
            HttpStatusCode.Accepted,
            await runtimeWriteResponse.Content.ReadAsStringAsync());

        using var ownerALlm = await GetJsonAsync(
            host.Client,
            $"/api/user-config/llm?scopeId={CrossScopeHint}",
            ownerAToken);
        using var ownerBLlm = await GetJsonAsync(
            host.Client,
            $"/api/user-config/llm?scopeId={CrossScopeHint}",
            ownerBToken);
        var ownerASelection = ownerALlm.RootElement.GetProperty("savedSelection");
        ownerASelection.GetProperty("routeKind").GetString().Should().Be("gateway");
        ownerASelection.GetProperty("modelSelection").GetProperty("modelId").GetString().Should().Be(OwnerAModel);
        ownerBLlm.RootElement.TryGetProperty("savedSelection", out _).Should().BeFalse();

        using var ownerARuntime = await GetJsonAsync(
            host.Client,
            $"/api/user-config/runtime?scopeId={CrossScopeHint}",
            ownerAToken);
        using var ownerBRuntime = await GetJsonAsync(
            host.Client,
            $"/api/user-config/runtime?scopeId={CrossScopeHint}",
            ownerBToken);
        ownerARuntime.RootElement.GetProperty("runtimeMode").GetString().Should().Be("remote");
        ownerARuntime.RootElement.GetProperty("activeRuntimeBaseUrl").GetString().Should().Be(OwnerARuntimeUrl);
        ownerBRuntime.RootElement.GetProperty("runtimeMode").GetString().Should().Be("local");
        ownerBRuntime.RootElement.GetProperty("activeRuntimeBaseUrl").GetString()
            .Should().Be(UserConfigRuntimeDefaults.LocalRuntimeBaseUrl);

        host.SecretsStore.GetApiKey(ProviderName).Should().Be(RawHostSecret);
    }

    private static async Task AssertOpenApiAsync(HttpClient client, string token)
    {
        using var openApi = await GetJsonAsync(client, "/api/openapi.json", token);
        var body = openApi.RootElement.GetRawText();
        AssertNoSensitiveValues(body);

        var paths = openApi.RootElement.GetProperty("paths");
        paths.TryGetProperty("/api/user-config/llm", out _).Should().BeTrue();
        paths.TryGetProperty("/api/user-config/runtime", out _).Should().BeTrue();
        paths.EnumerateObject().Select(static path => path.Name).Should().NotContain(route => IsLegacySettingsPath(route));
    }

    private static IEnumerable<HttpRequestMessage> CreateLegacyRequests(string? scopeHint)
    {
        var query = string.IsNullOrWhiteSpace(scopeHint)
            ? string.Empty
            : $"?scopeId={Uri.EscapeDataString(scopeHint)}";

        yield return new HttpRequestMessage(HttpMethod.Get, $"/api/settings{query}");
        yield return new HttpRequestMessage(HttpMethod.Put, $"/api/settings{query}")
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
        };
        yield return new HttpRequestMessage(HttpMethod.Post, $"/api/settings/runtime/test{query}")
        {
            Content = JsonContent(new { runtimeBaseUrl = "https://runtime.example.test" }),
        };
    }

    private static HttpRequestMessage CreateAuthenticatedJsonRequest(
        HttpMethod method,
        string path,
        string token,
        object value)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent(value),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        AssertNoSensitiveValues(body);
        return JsonDocument.Parse(body);
    }

    private static void AssertNoSensitiveValues(string body)
    {
        body.Should().NotContain(RawHostSecret);
        body.Should().NotContain(ReplacementSecret);
        body.Should().NotContain(CrossScopeHint);
    }

    private static bool IsLegacySettingsPath(string route) =>
        route.Equals("/api/settings", StringComparison.OrdinalIgnoreCase) ||
        route.StartsWith("/api/settings/", StringComparison.OrdinalIgnoreCase);

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
            [$"{AevatarAuthenticationOptions.SectionName}:Enabled"] = "true",
            [$"{AuditActorIdentityHasherOptions.SectionName}:ActiveKeyId"] = "test-key-1",
            [$"{AuditActorIdentityHasherOptions.SectionName}:Keys:0:KeyId"] = "test-key-1",
            [$"{AuditActorIdentityHasherOptions.SectionName}:Keys:0:Key"] =
                "mainnet settings endpoint security audit identity key",
            ["Projection:Document:Providers:InMemory:Enabled"] = "true",
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
            ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
            ["Projection:Graph:Providers:Neo4j:Enabled"] = "false",
            ["Aevatar:NyxId:Authority"] = "https://nyxid.example.test",
            ["Aevatar:NyxId:AssistantActions:Enabled"] = "false",
            [$"LLMProviders:Providers:{ProviderName}:ApiKey"] = RawHostSecret,
        });
        return builder;
    }

    private sealed record LegacyCaller(
        string Name,
        string? Token,
        string? ScopeHint,
        HttpStatusCode ExpectedStatus);

    private sealed class MainnetTestHost : IAsyncDisposable
    {
        private readonly HttpClient _client;
        private readonly RSA _rsa;
        private readonly IDisposable[] _environmentScopes;
        private readonly RsaSecurityKey _signingKey;

        private MainnetTestHost(
            WebApplication app,
            HttpClient client,
            RSA rsa,
            RsaSecurityKey signingKey,
            OwnerScopedUserConfigPort userConfigs,
            IDisposable[] environmentScopes)
        {
            App = app;
            _client = client;
            _rsa = rsa;
            _signingKey = signingKey;
            UserConfigs = userConfigs;
            _environmentScopes = environmentScopes;
        }

        public WebApplication App { get; }

        public HttpClient Client => _client;

        public IAevatarSecretsStore SecretsStore => App.Services.GetRequiredService<IAevatarSecretsStore>();

        public OwnerScopedUserConfigPort UserConfigs { get; }

        public static async Task<MainnetTestHost> StartAsync()
        {
            IDisposable[] environmentScopes =
            [
                new TemporaryAevatarHomeScope(),
                new EnvironmentVariableScope("AEVATAR_ActorRuntime__Provider", "InMemory"),
                new EnvironmentVariableScope("AEVATAR_Projection__Document__Providers__InMemory__Enabled", "true"),
                new EnvironmentVariableScope("AEVATAR_Projection__Document__Providers__Elasticsearch__Enabled", "false"),
                new EnvironmentVariableScope("AEVATAR_Projection__Graph__Providers__InMemory__Enabled", "true"),
                new EnvironmentVariableScope("AEVATAR_Projection__Graph__Providers__Neo4j__Enabled", "false"),
                new EnvironmentVariableScope("Projection__Policies__Environment", "Development"),
                new EnvironmentVariableScope("Projection__Policies__DenyInMemoryDocumentReadStore", "false"),
                new EnvironmentVariableScope("Projection__Policies__DenyInMemoryGraphFactStore", "false"),
                new EnvironmentVariableScope("AEVATAR_Aevatar__Authentication__Enabled", "true"),
            ];
            var rsa = RSA.Create(2048);
            var signingKey = new RsaSecurityKey(rsa) { KeyId = "mainnet-settings-security-test" };

            try
            {
                var builder = CreateBuilder();
                builder.WebHost.UseTestServer();
                builder.AddAevatarMainnetHost(options =>
                {
                    options.EnableConnectorBootstrap = false;
                    options.EnableCors = false;
                });
                builder.Services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options => ConfigureTestTokenValidation(options, signingKey));
                builder.Services.AddSingleton<OwnerScopedUserConfigPort>();
                builder.Services.Replace(ServiceDescriptor.Singleton<IUserConfigQueryPort>(serviceProvider =>
                    serviceProvider.GetRequiredService<OwnerScopedUserConfigPort>()));
                builder.Services.Replace(ServiceDescriptor.Singleton<IUserConfigCommandService>(serviceProvider =>
                    serviceProvider.GetRequiredService<OwnerScopedUserConfigPort>()));
                builder.Services.Replace(ServiceDescriptor.Singleton<IUserLlmCatalogPort>(
                    new SecurityTestUserLlmCatalogPort()));

                var app = builder.Build();
                app.MapAevatarMainnetHost();
                await app.StartAsync();

                var userConfigs = app.Services.GetRequiredService<OwnerScopedUserConfigPort>();
                return new MainnetTestHost(
                    app,
                    app.GetTestClient(),
                    rsa,
                    signingKey,
                    userConfigs,
                    environmentScopes);
            }
            catch
            {
                rsa.Dispose();
                foreach (var scope in environmentScopes.Reverse())
                    scope.Dispose();
                throw;
            }
        }

        public string CreateToken(string subject, string scopeId, bool platformAdmin = false)
        {
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, subject),
                new("scope_id", scopeId),
            };
            if (platformAdmin)
                claims.Add(new Claim(ClaimTypes.Role, "platform-admin"));

            return new JwtSecurityTokenHandler().CreateEncodedJwt(new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
            });
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            _rsa.Dispose();
            foreach (var scope in _environmentScopes.Reverse())
                scope.Dispose();
        }

        private static void ConfigureTestTokenValidation(JwtBearerOptions options, SecurityKey signingKey)
        {
            options.TokenValidationParameters.IssuerSigningKey = signingKey;
            options.TokenValidationParameters.ValidateIssuerSigningKey = true;
            options.TokenValidationParameters.ValidateIssuer = false;
            options.TokenValidationParameters.ValidateAudience = false;
            options.TokenValidationParameters.ValidateLifetime = true;
            options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
            options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
        }
    }

    private sealed class OwnerScopedUserConfigPort(IAppScopeResolver scopeResolver)
        : IUserConfigQueryPort, IUserConfigCommandService
    {
        private readonly Dictionary<UserConfigResourceKey, UserConfig> _configs = [];

        public IReadOnlyCollection<UserConfigResourceKey> Resources => _configs.Keys;

        public Task<UserConfig> GetAsync(
            UserConfigResourceKey resource,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_configs.GetValueOrDefault(resource) ?? new UserConfig(string.Empty));
        }

        public Task<UserConfig> GetAsync(CancellationToken ct = default) =>
            GetAsync(ResolveOwnerResource(), ct);

        public Task<UserConfigSaveReceipt> UpdateAsync(
            UserConfigResourceKey resource,
            UserConfigUpdate update,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var current = _configs.GetValueOrDefault(resource) ?? new UserConfig(string.Empty);
            var selection = update.LlmSelection ?? current.LlmSelection;
            _configs[resource] = current with
            {
                DefaultModel = update.LlmSelection is null
                    ? current.DefaultModel
                    : LLMSelectionPolicy.CompatibilityDefaultModel(update.LlmSelection),
                PreferredLlmRoute = update.LlmSelection is null
                    ? current.PreferredLlmRoute
                    : LLMSelectionPolicy.CompatibilityRoute(update.LlmSelection),
                LlmSelection = selection,
                RuntimeMode = update.RuntimeMode ?? current.RuntimeMode,
                LocalRuntimeBaseUrl = update.LocalRuntimeBaseUrl ?? current.LocalRuntimeBaseUrl,
                RemoteRuntimeBaseUrl = update.RemoteRuntimeBaseUrl ?? current.RemoteRuntimeBaseUrl,
                GithubUsername = update.GithubUsername ?? current.GithubUsername,
                MaxToolRounds = update.MaxToolRounds ?? current.MaxToolRounds,
            };

            return Task.FromResult(new UserConfigSaveReceipt(
                Accepted: true,
                CommandId: $"command-{resource.Value}",
                AckStage: UserConfigCommandAckStage.Accepted,
                ActorId: $"user-config:{resource.Value}",
                CorrelationId: $"correlation-{resource.Value}",
                AckedAtUtc: DateTimeOffset.UnixEpoch));
        }

        private UserConfigResourceKey ResolveOwnerResource() =>
            UserConfigResourceKey.ForOwnerScope(scopeResolver.ResolveScopeIdOrDefault());
    }

    private sealed class SecurityTestUserLlmCatalogPort : IUserLlmCatalogPort
    {
        public Task<NyxIdLlmServicesResult> GetServicesAsync(string bearerToken, CancellationToken ct) =>
            Task.FromResult(new NyxIdLlmServicesResult(
                [
                    new NyxIdLlmService(
                        CatalogEntryId: null,
                        ServiceSlug: "gateway",
                        DisplayName: "Gateway",
                        RouteValue: UserConfigLlmRouteDefaults.Gateway,
                        ModelCatalog: new LLMModelCatalog
                        {
                            Certainty = LLMModelCatalogCertainty.Enumerated,
                            DefaultModelId = OwnerAModel,
                            ModelIds = { OwnerAModel },
                        },
                        Status: UserLlmRouteStatus.Ready,
                        Source: UserLlmRouteSource.GatewayProvider,
                        Allowed: true,
                        Description: null),
                ],
                SetupHint: null));

        public Task<NyxIdLlmServicesResult> GetFreshServicesAsync(string bearerToken, CancellationToken ct) =>
            GetServicesAsync(bearerToken, ct);

        public Task<NyxIdLlmService> ProvisionAsync(
            string bearerToken,
            string provisionEndpointId,
            CancellationToken ct) =>
            throw new NotSupportedException("Provisioning is not part of the owner-scope security test.");
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
