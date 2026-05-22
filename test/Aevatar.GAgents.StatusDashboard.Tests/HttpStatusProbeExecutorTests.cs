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
        Action<HttpRequestMessage>? captureRequest = null)
    {
        var configRoot = new ConfigurationBuilder()
            .AddInMemoryCollection(configuration)
            .Build();
        var factory = new TestHttpClientFactory(new StubHandler(status, captureRequest));
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

    private sealed class StubHandler(HttpStatusCode status, Action<HttpRequestMessage>? captureRequest)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            captureRequest?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
