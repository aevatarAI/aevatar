using System.Net;
using Aevatar.GAgents.StatusDashboard.Executors;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgents.StatusDashboard.Tests;

public sealed class HttpStatusProbeExecutorTests
{
    [Fact]
    public async Task ReturnsOk_WhenStatusCodeMatchesExpectedSet()
    {
        var executor = NewExecutor(
            HttpStatusCode.Unauthorized,
            configuration: new Dictionary<string, string?>());
        var descriptor = NewDescriptor("nyxid-llm", new()
        {
            ["Url"] = "https://example.test/api/v1/llm/services",
            ["ExpectedStatuses"] = "200,401",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        outcome.Detail.Should().Be("http_401");
    }

    [Fact]
    public async Task ReturnsDown_WhenStatusCodeOutsideExpectedSet()
    {
        var executor = NewExecutor(
            HttpStatusCode.InternalServerError,
            configuration: new Dictionary<string, string?>());
        var descriptor = NewDescriptor("upstream", new()
        {
            ["Url"] = "https://example.test/healthz",
            ["ExpectedStatuses"] = "200",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("http_500");
        outcome.ErrorMessage.Should().Contain("Unexpected status 500");
    }

    [Fact]
    public async Task DegradesInsteadOfDown_WhenFlagSet()
    {
        var executor = NewExecutor(
            HttpStatusCode.ServiceUnavailable,
            configuration: new Dictionary<string, string?>());
        var descriptor = NewDescriptor("ready", new()
        {
            ["Url"] = "https://example.test/ready",
            ["ExpectedStatuses"] = "200",
            ["DegradedOnNon2xx"] = "true",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Degraded);
    }

    [Fact]
    public async Task ResolvesConfigurationPlaceholders()
    {
        Uri? captured = null;
        var executor = NewExecutor(
            HttpStatusCode.OK,
            configuration: new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:Authority"] = "https://nyx.example.test",
            },
            captureRequest: req => captured = req.RequestUri);

        var descriptor = NewDescriptor("nyxid-llm", new()
        {
            ["Url"] = "${configuration:Aevatar:NyxId:Authority}/api/v1/llm/services",
        });

        await executor.ProbeAsync(descriptor, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.ToString().Should().Be("https://nyx.example.test/api/v1/llm/services");
    }

    [Fact]
    public async Task ResolvesConfigurationPlaceholdersInHeaders()
    {
        string? capturedAuthorization = null;
        var executor = NewExecutor(
            HttpStatusCode.OK,
            configuration: new Dictionary<string, string?>
            {
                ["Aevatar:Status:AuthenticatedProbe:BearerToken"] = "token-from-pod",
            },
            captureRequest: req =>
            {
                capturedAuthorization = req.Headers.Authorization?.ToString();
            });

        var descriptor = NewDescriptor("authenticated-probe", new()
        {
            ["Url"] = "https://example.test/v1/responses",
            ["Header.Authorization"] =
                "Bearer ${configuration:Aevatar:Status:AuthenticatedProbe:BearerToken}",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        capturedAuthorization.Should().Be("Bearer token-from-pod");
    }

    [Fact]
    public async Task UsesClientCredentialsGrant_ForDynamicAuthorization()
    {
        var requests = new List<CapturedRequest>();
        var executor = NewExecutor(
            async req =>
            {
                requests.Add(await CloneRequestAsync(req));
                if (req.RequestUri?.AbsoluteUri == "https://nyx.example.test/oauth/token")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {"access_token":"fresh-access","token_type":"Bearer","expires_in":28800}
                        """),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            configuration: new Dictionary<string, string?>
            {
                ["Aevatar:Status:AuthenticatedProbe:BearerToken"] = "expired-access",
                ["Aevatar:Status:AuthenticatedProbe:ClientId"] = "client-id",
                ["Aevatar:Status:AuthenticatedProbe:ClientSecret"] = "client-secret",
            });

        var descriptor = NewDescriptor("authenticated-probe", new()
        {
            ["Url"] = "https://example.test/v1/responses",
            ["Header.Authorization"] =
                "Bearer ${configuration:Aevatar:Status:AuthenticatedProbe:BearerToken}",
            ["Auth.Mode"] = "auto",
            ["Auth.ClientIdConfigurationKey"] = "Aevatar:Status:AuthenticatedProbe:ClientId",
            ["Auth.ClientSecretConfigurationKey"] = "Aevatar:Status:AuthenticatedProbe:ClientSecret",
            ["Auth.ClientCredentialsScope"] = "proxy:* llm:proxy",
            ["Auth.TokenEndpoint"] = "https://nyx.example.test/oauth/token",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        requests.Should().HaveCount(2);
        requests[0].RequestUri!.AbsoluteUri.Should().Be("https://nyx.example.test/oauth/token");
        requests[0].ContentText.Should().Contain("grant_type=client_credentials");
        requests[0].ContentText.Should().Contain("client_id=client-id");
        requests[0].ContentText.Should().Contain("client_secret=client-secret");
        requests[0].ContentText.Should().Contain("scope=proxy");
        requests[1].RequestUri!.AbsoluteUri.Should().Be("https://example.test/v1/responses");
        requests[1].Authorization.Should().Be("Bearer fresh-access");
    }

    [Fact]
    public async Task CachesClientCredentialsAccessToken()
    {
        var tokenCalls = 0;
        var executor = NewExecutor(
            req =>
            {
                if (req.RequestUri?.AbsoluteUri == "https://nyx.example.test/oauth/token")
                {
                    tokenCalls++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {"access_token":"fresh-access","token_type":"Bearer","expires_in":28800}
                        """),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            configuration: new Dictionary<string, string?>
            {
                ["Aevatar:Status:AuthenticatedProbe:ClientId"] = "client-id",
                ["Aevatar:Status:AuthenticatedProbe:ClientSecret"] = "client-secret",
            });

        var descriptor = NewDescriptor("authenticated-probe", new()
        {
            ["Url"] = "https://example.test/v1/responses",
            ["Header.Authorization"] = "Bearer stale",
            ["Auth.Mode"] = "auto",
            ["Auth.ClientIdConfigurationKey"] = "Aevatar:Status:AuthenticatedProbe:ClientId",
            ["Auth.ClientSecretConfigurationKey"] = "Aevatar:Status:AuthenticatedProbe:ClientSecret",
            ["Auth.TokenEndpoint"] = "https://nyx.example.test/oauth/token",
        });

        await executor.ProbeAsync(descriptor, CancellationToken.None);
        await executor.ProbeAsync(descriptor, CancellationToken.None);

        tokenCalls.Should().Be(1);
    }

    [Fact]
    public async Task ReturnsDown_WhenClientCredentialsGrantFails()
    {
        var executor = NewExecutor(
            req => req.RequestUri?.AbsoluteUri == "https://nyx.example.test/oauth/token"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : new HttpResponseMessage(HttpStatusCode.OK),
            configuration: new Dictionary<string, string?>
            {
                ["Aevatar:Status:AuthenticatedProbe:ClientId"] = "client-id",
                ["Aevatar:Status:AuthenticatedProbe:ClientSecret"] = "client-secret",
            });

        var descriptor = NewDescriptor("authenticated-probe", new()
        {
            ["Url"] = "https://example.test/v1/responses",
            ["Header.Authorization"] = "Bearer stale",
            ["Auth.Mode"] = "auto",
            ["Auth.ClientIdConfigurationKey"] = "Aevatar:Status:AuthenticatedProbe:ClientId",
            ["Auth.ClientSecretConfigurationKey"] = "Aevatar:Status:AuthenticatedProbe:ClientSecret",
            ["Auth.TokenEndpoint"] = "https://nyx.example.test/oauth/token",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("oauth_token_failed");
        outcome.ErrorMessage.Should().Contain("HTTP 401");
    }

    [Fact]
    public async Task ReturnsOk_WhenExpectedBodyContainsAndRegexMatch()
    {
        var executor = NewExecutor(
            HttpStatusCode.OK,
            configuration: new Dictionary<string, string?>(),
            responseBody: """
            event: response.completed
            data: {"id":"resp_1","status":"completed"}
            """);
        var descriptor = NewDescriptor("responses", new()
        {
            ["Url"] = "https://example.test/v1/responses",
            ["ExpectedBodyContains"] = "event: response.completed",
            ["ExpectedBodyRegex"] = "\"status\"\\s*:\\s*\"completed\"",
            ["ForbiddenBodyContains"] = "event: response.failed",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        outcome.Detail.Should().Be("http_200");
    }

    [Fact]
    public async Task ReturnsDown_WhenExpectedBodyMarkerIsMissing()
    {
        var executor = NewExecutor(
            HttpStatusCode.OK,
            configuration: new Dictionary<string, string?>(),
            responseBody: """{"status":"accepted"}""");
        var descriptor = NewDescriptor("responses", new()
        {
            ["Url"] = "https://example.test/v1/responses",
            ["ExpectedBodyContains"] = "event: response.completed",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("body_assertion_failed");
        outcome.ErrorMessage.Should().Contain("Expected response body marker");
    }

    [Fact]
    public async Task ReturnsDown_WhenForbiddenBodyMarkerAppears()
    {
        var executor = NewExecutor(
            HttpStatusCode.OK,
            configuration: new Dictionary<string, string?>(),
            responseBody: """
            event: response.failed
            data: {"error":{"message":"hidden upstream detail"}}
            """);
        var descriptor = NewDescriptor("responses", new()
        {
            ["Url"] = "https://example.test/v1/responses",
            ["ForbiddenBodyContains"] = "event: response.failed",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("body_assertion_failed");
        outcome.ErrorMessage.Should().Be("Forbidden response body marker was found.");
        outcome.ErrorMessage.Should().NotContain("hidden upstream detail");
    }

    [Fact]
    public async Task ReportsMissingUrlAsDown()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-21T10:00:00Z"));
        var executor = NewExecutor(HttpStatusCode.OK, configuration: new Dictionary<string, string?>(), timeProvider: clock);
        var descriptor = NewDescriptor("missing-url", new() { ["ExpectedStatuses"] = "200" });
        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);
        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("missing_parameter");
        outcome.ObservedAt.ToDateTimeOffset().Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public async Task ScopeServiceToken_ReturnsUnknown_WhenProviderUnavailable()
    {
        var executor = NewExecutor(
            HttpStatusCode.OK,
            new Dictionary<string, string?>(),
            scopeTokenProvider: null);
        var descriptor = NewDescriptor("orchestration", new()
        {
            ["Url"] = "https://example.test/api/scopes/s1/services",
            ["Auth.Mode"] = "scope_service_token",
            ["Auth.ScopeId"] = "s1",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);

        // No issuer configured → degrade to unknown, never a false down or an unauthenticated 401.
        outcome.Status.Should().Be(HealthOutcomeStatus.Unknown);
        outcome.Detail.Should().Be("credential_unavailable");
    }

    [Fact]
    public async Task ScopeServiceToken_AttachesMintedBearer_WithoutHeaderParam()
    {
        string? capturedAuthorization = null;
        var executor = NewExecutor(
            HttpStatusCode.OK,
            new Dictionary<string, string?>(),
            scopeTokenProvider: new FakeScopeTokenProvider("minted-scope-token"),
            captureRequest: req => capturedAuthorization = req.Headers.Authorization?.ToString());
        var descriptor = NewDescriptor("orchestration", new()
        {
            ["Url"] = "https://example.test/api/scopes/s1/services",
            ["ExpectedStatuses"] = "200",
            ["Auth.Mode"] = "scope_service_token",
            ["Auth.ScopeId"] = "s1",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        capturedAuthorization.Should().Be("Bearer minted-scope-token");
    }

    [Fact]
    public async Task StaticBearer_AttachesConfiguredToken_WithoutHeaderParam()
    {
        string? capturedAuthorization = null;
        var executor = NewExecutor(
            HttpStatusCode.OK,
            new Dictionary<string, string?> { ["Aevatar:Status:Probe:CanaryBearer"] = "canary-key" },
            scopeTokenProvider: null,
            captureRequest: req => capturedAuthorization = req.Headers.Authorization?.ToString());
        var descriptor = NewDescriptor("llm-canary", new()
        {
            ["Url"] = "https://example.test/v1/models",
            ["ExpectedStatuses"] = "200",
            ["Auth.Mode"] = "static_bearer",
            ["Auth.StaticBearerConfigurationKey"] = "Aevatar:Status:Probe:CanaryBearer",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        capturedAuthorization.Should().Be("Bearer canary-key");
    }

    private static HttpStatusProbeExecutor NewExecutor(
        HttpStatusCode status,
        Dictionary<string, string?> configuration,
        IProbeServiceTokenProvider? scopeTokenProvider,
        Action<HttpRequestMessage>? captureRequest = null)
    {
        var configRoot = new ConfigurationBuilder()
            .AddInMemoryCollection(configuration)
            .Build();
        var factory = new TestHttpClientFactory(new StubHandler(status, captureRequest, null));
        var providers = scopeTokenProvider is null
            ? Array.Empty<IProbeServiceTokenProvider>()
            : new[] { scopeTokenProvider };
        return new HttpStatusProbeExecutor(
            factory,
            configRoot,
            new StatusProbeAuthorizationResolver(factory, configRoot, providers),
            TimeProvider.System);
    }

    private sealed class FakeScopeTokenProvider(string? token) : IProbeServiceTokenProvider
    {
        public Task<string?> GetScopeBearerAsync(string scopeId, CancellationToken ct) =>
            Task.FromResult(token);
    }

    private static HttpStatusProbeExecutor NewExecutor(
        HttpStatusCode status,
        Dictionary<string, string?> configuration,
        Action<HttpRequestMessage>? captureRequest = null,
        string? responseBody = null,
        TimeProvider? timeProvider = null)
    {
        var configRoot = new ConfigurationBuilder()
            .AddInMemoryCollection(configuration)
            .Build();
        var factory = new TestHttpClientFactory(new StubHandler(status, captureRequest, responseBody));
        return new HttpStatusProbeExecutor(
            factory,
            configRoot,
            new StatusProbeAuthorizationResolver(factory, configRoot),
            timeProvider ?? TimeProvider.System);
    }

    private static HttpStatusProbeExecutor NewExecutor(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler,
        Dictionary<string, string?> configuration)
    {
        var configRoot = new ConfigurationBuilder()
            .AddInMemoryCollection(configuration)
            .Build();
        var factory = new TestHttpClientFactory(new DelegateHandler(handler));
        return new HttpStatusProbeExecutor(
            factory,
            configRoot,
            new StatusProbeAuthorizationResolver(factory, configRoot),
            TimeProvider.System);
    }

    private static HttpStatusProbeExecutor NewExecutor(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        Dictionary<string, string?> configuration) =>
        NewExecutor(req => Task.FromResult(handler(req)), configuration);

    private static HealthProbeTargetDescriptor NewDescriptor(string slug, Dictionary<string, string> parameters)
    {
        var d = new HealthProbeTargetDescriptor
        {
            Slug = slug,
            DisplayName = slug,
            Category = "upstream",
            ProbeKind = "http_status",
            IntervalSeconds = 60,
            TimeoutMs = 5_000,
            Enabled = true,
        };
        foreach (var (k, v) in parameters) d.Parameters[k] = v;
        return d;
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private sealed class StubHandler(
        HttpStatusCode status,
        Action<HttpRequestMessage>? captureRequest,
        string? responseBody)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            captureRequest?.Invoke(request);
            var response = new HttpResponseMessage(status);
            if (responseBody is not null)
                response.Content = new StringContent(responseBody);
            return Task.FromResult(response);
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            await handler(request);
    }

    private static async Task<CapturedRequest> CloneRequestAsync(HttpRequestMessage request)
    {
        return new CapturedRequest(
            request.RequestUri,
            request.Headers.Authorization?.ToString(),
            request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync());
    }

    private sealed record CapturedRequest(
        Uri? RequestUri,
        string? Authorization,
        string ContentText);
}
