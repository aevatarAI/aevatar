using System.Net;
using Aevatar.GAgents.StatusDashboard.Executors;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

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
                ["Aevatar:Status:ResponsesForwardToTeam:BearerToken"] = "token-from-pod",
            },
            captureRequest: req =>
            {
                capturedAuthorization = req.Headers.Authorization?.ToString();
            });

        var descriptor = NewDescriptor("responses-forward-team", new()
        {
            ["Url"] = "https://example.test/v1/responses",
            ["Header.Authorization"] =
                "Bearer ${configuration:Aevatar:Status:ResponsesForwardToTeam:BearerToken}",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        capturedAuthorization.Should().Be("Bearer token-from-pod");
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
        var executor = NewExecutor(HttpStatusCode.OK, configuration: new Dictionary<string, string?>());
        var descriptor = NewDescriptor("missing-url", new() { ["ExpectedStatuses"] = "200" });
        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);
        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("missing_parameter");
    }

    private static HttpStatusProbeExecutor NewExecutor(
        HttpStatusCode status,
        Dictionary<string, string?> configuration,
        Action<HttpRequestMessage>? captureRequest = null,
        string? responseBody = null)
    {
        var configRoot = new ConfigurationBuilder()
            .AddInMemoryCollection(configuration)
            .Build();
        var factory = new TestHttpClientFactory(new StubHandler(status, captureRequest, responseBody));
        return new HttpStatusProbeExecutor(factory, configRoot);
    }

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

    private sealed class TestHttpClientFactory(StubHandler handler) : IHttpClientFactory
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
}
