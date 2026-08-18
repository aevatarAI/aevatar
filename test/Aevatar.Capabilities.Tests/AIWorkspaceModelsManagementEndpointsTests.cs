using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.Mainnet.Host.Api.AI;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aevatar.Capabilities.Tests;

public sealed class AIWorkspaceModelsManagementEndpointsTests
{
    private const string ScopeId = "scope-alpha";

    [Fact]
    public void Mapping_ShouldRequireAuthorizationAuditEveryOperationAndKeepPathsUnique()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();

        app.MapAIWorkspaceEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(static endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                "/api/ai/models",
                StringComparison.Ordinal) == true)
            .SelectMany(static endpoint =>
                (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => new
                {
                    Key = $"{method} {endpoint.RoutePattern.RawText}",
                    Authorized = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null,
                    AuditOperation = endpoint.Metadata.GetMetadata<EndpointAuditMetadata>()?.OperationName,
                }))
            .ToArray();

        routes.Select(static route => route.Key).Should().OnlyHaveUniqueItems();
        routes.Should().OnlyContain(static route => route.Authorized);
        routes.Should().OnlyContain(static route => route.AuditOperation != null);
        routes.Select(static route => route.Key).Should().BeEquivalentTo([
            "GET /api/ai/models",
            "GET /api/ai/models/personal-default",
            "PUT /api/ai/models/personal-default",
            "GET /api/ai/models/catalog",
            "PUT /api/ai/models/catalog",
            "DELETE /api/ai/models/catalog",
            "GET /api/ai/models/catalog/candidates",
            "GET /api/ai/models/catalog/candidates/{userServiceId}/models",
        ]);
    }

    [Fact]
    public async Task PersonalDefault_WithoutAuthentication_ShouldReturnUnauthorized()
    {
        await using var host = await ModelsTestHost.StartAsync();

        var response = await host.Client.GetAsync("/api/ai/models/personal-default");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PersonalDefault_WhenApplicationErrorContainsPartitionVocabulary_ShouldReturnNeutralAIError()
    {
        var preferences = Substitute.For<IUserLlmPreferenceService>();
        preferences.GetSettingsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<UserLlmSettingsView>(
                new InvalidOperationException(
                    "scopeCatalog is unavailable for this authorization partition.")));
        await using var host = await ModelsTestHost.StartAsync(preferences: preferences);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", ScopeId);

        var response = await host.Client.GetAsync("/api/ai/models/personal-default");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        AssertAIError(
            body,
            "AI_PERSONAL_MODEL_DEFAULT_INVALID",
            "Personal model settings request is invalid.");
        AssertNoAuthorizationPartitionVocabulary(body);
    }

    [Fact]
    public async Task Catalog_ShouldDeriveScopeOnlyFromVerifiedCallerClaim()
    {
        var catalog = Substitute.For<ILLMModelCatalogPolicyApplicationService>();
        catalog.GetScopeAsync(ScopeId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(EmptyCatalog(ScopeId)));
        await using var host = await ModelsTestHost.StartAsync(catalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", ScopeId);

        var response = await host.Client.GetAsync("/api/ai/models/catalog");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using (var json = JsonDocument.Parse(body))
        {
            json.RootElement.TryGetProperty("scopeId", out _).Should().BeFalse();
            json.RootElement.TryGetProperty("ownerKind", out _).Should().BeFalse();
            json.RootElement.TryGetProperty("authorityKind", out _).Should().BeFalse();
            json.RootElement.GetProperty("effectiveSource").GetString().Should().Be("platform");
        }
        await catalog.Received(1).GetScopeAsync(ScopeId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Catalog_WithCallerCustomPolicy_ShouldUseProductSourceVocabulary()
    {
        var catalog = Substitute.For<ILLMModelCatalogPolicyApplicationService>();
        catalog.GetScopeAsync(ScopeId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LLMModelCatalogView(
                LLMModelCatalogPolicyOwner.ForScope(ScopeId),
                LLMModelCatalogPolicyMode.Custom,
                true,
                4,
                DateTimeOffset.Parse("2026-08-18T10:00:00Z"),
                [],
                LLMModelCatalogEffectiveSourceKind.Scope,
                [],
                "mutation-alpha")));
        await using var host = await ModelsTestHost.StartAsync(catalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", ScopeId);

        var response = await host.Client.GetAsync("/api/ai/models/catalog");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("effectiveSource").GetString().Should().Be("custom");
        body.Should().NotContain("scope-alpha");
        body.Should().NotContain("\"scope\"");
    }

    [Theory]
    [InlineData("catalogServiceId")]
    [InlineData("scopeId")]
    [InlineData("teamId")]
    [InlineData("ownerKind")]
    public async Task PutCatalog_WithUnknownSourceField_ShouldRejectBeforeApplicationCall(
        string unknownField)
    {
        var catalog = Substitute.For<ILLMModelCatalogPolicyApplicationService>();
        await using var host = await ModelsTestHost.StartAsync(catalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", ScopeId);

        var source = new Dictionary<string, object?>
        {
            ["userServiceId"] = "user-service-alpha",
            ["modelSelection"] = new
            {
                mode = "explicit_models",
                modelIds = new[] { "gpt-5.5" },
            },
            [unknownField] = "internal-alpha",
        };

        var response = await host.Client.PutAsJsonAsync(
            "/api/ai/models/catalog",
            new
            {
                mode = "custom_replace",
                expectedVersion = 4,
                idempotencyKey = "mutation-alpha",
                sources = new[] { source },
            });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        await catalog.DidNotReceiveWithAnyArgs().ReplaceScopeAsync(default!, default!, default);
    }

    [Fact]
    public async Task PutCatalog_WithUnknownModelSelectionField_ShouldRejectBeforeApplicationCall()
    {
        var catalog = Substitute.For<ILLMModelCatalogPolicyApplicationService>();
        await using var host = await ModelsTestHost.StartAsync(catalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", ScopeId);

        var response = await host.Client.PutAsJsonAsync(
            "/api/ai/models/catalog",
            new
            {
                mode = "custom_replace",
                expectedVersion = 4,
                idempotencyKey = "mutation-alpha",
                sources = new[]
                {
                    new
                    {
                        userServiceId = "user-service-alpha",
                        modelSelection = new
                        {
                            mode = "explicit_models",
                            modelIds = new[] { "gpt-5.5" },
                            scopeId = "scope-alpha",
                        },
                    },
                },
            });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        await catalog.DidNotReceiveWithAnyArgs().ReplaceScopeAsync(default!, default!, default);
    }

    [Fact]
    public async Task Catalog_WhenApplicationErrorContainsPartitionVocabulary_ShouldReturnNeutralAIError()
    {
        var catalog = Substitute.For<ILLMModelCatalogPolicyApplicationService>();
        catalog.GetScopeAsync(ScopeId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<LLMModelCatalogView>(
                new LLMModelCatalogApplicationException(
                    LLMModelCatalogApplicationErrorKind.InvalidRequest,
                    "SCOPE_ID_TOO_LONG",
                    "scopeId must be shorter for this authorization partition.")));
        await using var host = await ModelsTestHost.StartAsync(catalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", ScopeId);

        var response = await host.Client.GetAsync("/api/ai/models/catalog");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        AssertAIError(
            body,
            "AI_MODEL_REQUEST_INVALID",
            "Model settings request is invalid.");
        AssertNoAuthorizationPartitionVocabulary(body);
    }

    [Fact]
    public async Task CatalogCandidates_WhenApplicationErrorsContainPartitionVocabulary_ShouldReturnNeutralAIErrors()
    {
        var catalog = Substitute.For<ILLMModelCatalogPolicyApplicationService>();
        catalog.GetScopeCandidatesAsync("user-token", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<NyxIdScopeModelSourceService>>(
                PartitionFailure()));
        catalog.DiscoverScopeModelsAsync(
                "user-token",
                "user-service-alpha",
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<LLMModelSourceDiscoveryView>(PartitionFailure()));
        await using var host = await ModelsTestHost.StartAsync(catalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", ScopeId);
        host.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "user-token");

        foreach (var path in new[]
                 {
                     "/api/ai/models/catalog/candidates",
                     "/api/ai/models/catalog/candidates/user-service-alpha/models",
                 })
        {
            var response = await host.Client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
            AssertAIError(
                body,
                "AI_MODEL_ACCESS_DENIED",
                "Model source access was denied.");
            AssertNoAuthorizationPartitionVocabulary(body);
        }
    }

    [Fact]
    public async Task CatalogCandidates_WithoutBearerToken_ShouldUseAIErrorContract()
    {
        var catalog = Substitute.For<ILLMModelCatalogPolicyApplicationService>();
        await using var host = await ModelsTestHost.StartAsync(catalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", ScopeId);

        foreach (var path in new[]
                 {
                     "/api/ai/models/catalog/candidates",
                     "/api/ai/models/catalog/candidates/user-service-alpha/models",
                 })
        {
            var response = await host.Client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
            AssertAIError(body, "AUTHENTICATION_REQUIRED", "Bearer token is required.");
        }

        await catalog.DidNotReceiveWithAnyArgs().GetScopeCandidatesAsync(default!, default);
        await catalog.DidNotReceiveWithAnyArgs().DiscoverScopeModelsAsync(default!, default!, default);
    }

    [Fact]
    public async Task Catalog_WithAmbiguousScopeClaims_ShouldFailClosedBeforeApplicationCall()
    {
        var catalog = Substitute.For<ILLMModelCatalogPolicyApplicationService>();
        await using var host = await ModelsTestHost.StartAsync(catalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha,scope-beta");

        var response = await host.Client.GetAsync("/api/ai/models/catalog");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        AssertAIError(
            body,
            "AI_ACCESS_CONTEXT_REQUIRED",
            "Authenticated caller access context is required.");
        await catalog.DidNotReceiveWithAnyArgs().GetScopeAsync(default!, default);
    }

    [Fact]
    public async Task PutCatalog_ShouldMapFacadeGuardsToCanonicalMutationIntent()
    {
        var catalog = Substitute.For<ILLMModelCatalogPolicyApplicationService>();
        catalog.ReplaceScopeAsync(ScopeId, Arg.Any<ReplaceScopeLLMModelCatalogIntent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Receipt()));
        await using var host = await ModelsTestHost.StartAsync(catalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", ScopeId);

        var response = await host.Client.PutAsJsonAsync(
            "/api/ai/models/catalog",
            new
            {
                mode = "custom_replace",
                expectedVersion = 4,
                idempotencyKey = "mutation-alpha",
                sources = Array.Empty<object>(),
            });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
        using (var json = JsonDocument.Parse(body))
        {
            json.RootElement.TryGetProperty("actorId", out _).Should().BeFalse();
            json.RootElement.GetProperty("commandId").GetString().Should().Be("command-alpha");
        }
        await catalog.Received(1).ReplaceScopeAsync(
            ScopeId,
            Arg.Is<ReplaceScopeLLMModelCatalogIntent>(intent =>
                intent.Mode == LLMModelCatalogPolicyMode.Custom &&
                intent.ExpectedStateVersion == 4 &&
                intent.MutationId == "mutation-alpha" &&
                intent.Sources != null &&
                intent.Sources.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteCatalog_ShouldReturnRedactedReceiptAndMapResetIntent()
    {
        var catalog = Substitute.For<ILLMModelCatalogPolicyApplicationService>();
        catalog.ResetScopeAsync(ScopeId, Arg.Any<LLMModelCatalogResetIntent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Receipt()));
        await using var host = await ModelsTestHost.StartAsync(catalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", ScopeId);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/ai/models/catalog")
        {
            Content = JsonContent.Create(new
            {
                expectedVersion = 7,
                idempotencyKey = "reset-alpha",
            }),
        };

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
        using (var json = JsonDocument.Parse(body))
        {
            json.RootElement.TryGetProperty("actorId", out _).Should().BeFalse();
            json.RootElement.GetProperty("commandId").GetString().Should().Be("command-alpha");
        }
        await catalog.Received(1).ResetScopeAsync(
            ScopeId,
            Arg.Is<LLMModelCatalogResetIntent>(intent =>
                intent.ExpectedStateVersion == 7 && intent.MutationId == "reset-alpha"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PutPersonalDefault_ShouldResolveRouteToTypedUserServiceIntent()
    {
        const string routeValue = "/api/v1/proxy/s/chrono-runtime";
        var preferences = Substitute.For<IUserLlmPreferenceService>();
        preferences.GetSettingsAsync("user-token", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SettingsWithRoute(routeValue)));
        var config = Substitute.For<IUserConfigService>();
        config.SaveLlmPreferenceAsync(
                "user-token",
                Arg.Any<UserLlmPreferenceIntent>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Receipt()));
        await using var host = await ModelsTestHost.StartAsync(preferences, config);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", ScopeId);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "user-token");

        var response = await host.Client.PutAsJsonAsync(
            "/api/ai/models/personal-default",
            new { routeValue, modelId = "gpt-5.5" });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
        using (var json = JsonDocument.Parse(body))
        {
            json.RootElement.TryGetProperty("actorId", out _).Should().BeFalse();
            json.RootElement.GetProperty("correlationId").GetString().Should().Be("correlation-alpha");
        }
        await config.Received(1).SaveLlmPreferenceAsync(
            "user-token",
            Arg.Is<SelectUserServiceUserLlmPreferenceIntent>(intent =>
                intent.UserServiceId == "user-service-alpha" &&
                intent.ModelSelection.Kind == LLMModelSelectionKind.ExplicitModel &&
                intent.ModelSelection.ModelId == "gpt-5.5"),
            Arg.Any<CancellationToken>());
    }

    private static LLMModelCatalogView EmptyCatalog(string scopeId) => new(
        LLMModelCatalogPolicyOwner.ForScope(scopeId),
        LLMModelCatalogPolicyMode.InheritPlatform,
        false,
        0,
        null,
        [],
        LLMModelCatalogEffectiveSourceKind.Platform,
        [],
        null);

    private static LLMModelCatalogApplicationException PartitionFailure() => new(
        LLMModelCatalogApplicationErrorKind.Forbidden,
        "SCOPE_CATALOG_FORBIDDEN",
        "Scope sources are forbidden by the authorization partition.");

    private static void AssertAIError(string body, string expectedCode, string expectedMessage)
    {
        using var json = JsonDocument.Parse(body);
        json.RootElement.EnumerateObject()
            .Select(static property => property.Name)
            .Should().BeEquivalentTo("code", "message");
        json.RootElement.GetProperty("code").GetString().Should().Be(expectedCode);
        json.RootElement.GetProperty("message").GetString().Should().Be(expectedMessage);
    }

    private static void AssertNoAuthorizationPartitionVocabulary(string body)
    {
        var normalized = body.ToLowerInvariant();
        normalized.Should().NotContain("scope");
        normalized.Should().NotContain("ownerkind");
        normalized.Should().NotContain("authoritykind");
        normalized.Should().NotContain("authorization partition");
    }

    private static UserLlmSettingsView SettingsWithRoute(string routeValue) => new(
        null,
        string.Empty,
        UserLlmSelectionStatus.SystemDefault,
        LLMModelCatalogDiagnosticKind.Unspecified,
        UserLlmRemediationKind.None,
        [
            new UserLlmRouteOption(
                routeValue,
                "Chrono Runtime",
                UserLlmRouteSource.UserService,
                UserLlmRouteStatus.Ready,
                true,
                true,
                "user-service-alpha",
                "chrono-runtime",
                new LLMModelCatalog
                {
                    Certainty = LLMModelCatalogCertainty.Enumerated,
                    ModelIds = { "gpt-5.5" },
                    DefaultModelId = "gpt-5.5",
                    DiagnosticKind = LLMModelCatalogDiagnosticKind.Unspecified,
                },
                null),
        ],
        [],
        UserLlmCatalogStatus.Ready,
        new UserLlmSettingsCapabilities(true, true, true, true),
        null);

    private static UserConfigSaveReceipt Receipt() => new(
        true,
        "command-alpha",
        UserConfigCommandAckStage.Accepted,
        "actor-alpha",
        "correlation-alpha",
        DateTimeOffset.Parse("2026-08-18T10:00:00Z"));

    private sealed class ModelsTestHost : IAsyncDisposable
    {
        private ModelsTestHost(WebApplication app)
        {
            App = app;
            Client = app.GetTestClient();
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public static async Task<ModelsTestHost> StartAsync(
            IUserLlmPreferenceService? preferences = null,
            IUserConfigService? config = null,
            ILLMModelCatalogPolicyApplicationService? catalog = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:Authentication:Enabled"] = "true",
            });
            builder.Services.AddAuthentication("test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton(preferences ?? Substitute.For<IUserLlmPreferenceService>());
            builder.Services.AddSingleton(config ?? Substitute.For<IUserConfigService>());
            builder.Services.AddSingleton(catalog ?? Substitute.For<ILLMModelCatalogPolicyApplicationService>());

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            var api = app.MapGroup("/api/ai").RequireAuthorization();
            api.MapAIWorkspaceModelsManagementEndpoints();
            await app.StartAsync();
            return new ModelsTestHost(app);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Scope", out var values))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = values.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static scopeId => new Claim("scope_id", scopeId))
                .Append(new Claim("sub", "subject-alpha"));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
