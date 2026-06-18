using System.Net;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Tests;

public class ConnectedServiceSpecCacheTests
{
    private const string GithubSpec = """
        {
          "openapi": "3.1.0",
          "paths": {
            "/repos": {
              "get": { "operationId": "list_repos", "summary": "List repos" }
            },
            "/repos/{owner}/{repo}": {
              "get": { "operationId": "get_repo", "summary": "Get repo" }
            }
          }
        }
        """;

    [Fact]
    public async Task GetOrFetchAsync_FetchesSpecWithoutProcessLocalSnapshot()
    {
        var handler = new FakeHttpHandler(GithubSpec);
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var cache = new ConnectedServiceSpecCache(options, http);

        var ops = await cache.GetOrFetchAsync("github", "svc-github", null, "token123");

        ops.Should().NotBeNull();
        ops.Should().HaveCount(2);
        ops![0].Service.Should().Be("github");
        handler.RequestCount.Should().Be(1);

        var ops2 = await cache.GetOrFetchAsync("github", "svc-github", null, "token123");
        ops2.Should().BeEquivalentTo(ops);
        handler.RequestCount.Should().Be(2, "spec hints must re-read NyxID instead of serving process-local OpenAPI facts");
    }

    [Fact]
    public async Task GetOrFetchAsync_UsesExplicitSpecUrl()
    {
        var handler = new FakeHttpHandler(GithubSpec);
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var cache = new ConnectedServiceSpecCache(options, http);

        await cache.GetOrFetchAsync("github", "svc-github", "https://custom.test/spec.json", "token123");

        handler.LastRequestUri.Should().Be("https://custom.test/spec.json");
    }

    [Fact]
    public async Task GetOrFetchAsync_ConstructsUrlFromBaseUrl_WhenNoSpecUrl()
    {
        var handler = new FakeHttpHandler(GithubSpec);
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var cache = new ConnectedServiceSpecCache(options, http);

        await cache.GetOrFetchAsync("github", "svc-github", null, "token123");

        handler.LastRequestUri.Should().Be("https://nyx.test/api/v1/proxy/services/svc-github/openapi.json");
    }

    [Fact]
    public async Task GetOrFetchAsync_SendsBearerToken_ToTrustedHost()
    {
        var handler = new FakeHttpHandler(GithubSpec);
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var cache = new ConnectedServiceSpecCache(options, http);

        await cache.GetOrFetchAsync("github", "svc-github", null, "my-secret-token");

        handler.LastAuthHeader.Should().Be("Bearer my-secret-token");
    }

    [Fact]
    public async Task GetOrFetchAsync_OmitsBearerToken_ForUntrustedHost()
    {
        var handler = new FakeHttpHandler(GithubSpec);
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var cache = new ConnectedServiceSpecCache(options, http);

        await cache.GetOrFetchAsync("github", "svc-github", "https://evil.test/spec.json", "my-secret-token");

        handler.LastAuthHeader.Should().BeNull("token must not be sent to untrusted hosts");
    }

    [Fact]
    public async Task GetOrFetchAsync_UsesLiveFetchForEachSpecUrl()
    {
        var handler = new FakeHttpHandler(GithubSpec);
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var cache = new ConnectedServiceSpecCache(options, http);

        await cache.GetOrFetchAsync("github", "svc-github", "https://nyx.test/v1/spec.json", "token");
        handler.RequestCount.Should().Be(1);

        await cache.GetOrFetchAsync("github", "svc-github", "https://nyx.test/v2/spec.json", "token");
        handler.RequestCount.Should().Be(2, "each request should read the requested NyxID/OpenAPI URL directly");
    }

    [Fact]
    public void IsTrustedHost_MatchesBaseUrl()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test:443" };
        using var cache = new ConnectedServiceSpecCache(options);

        cache.IsTrustedHost("https://nyx.test:443/api/v1/spec.json").Should().BeTrue();
        cache.IsTrustedHost("https://nyx.test/api/v1/spec.json").Should().BeTrue();
        cache.IsTrustedHost("https://evil.test/api/v1/spec.json").Should().BeFalse();
        cache.IsTrustedHost("http://nyx.test/api/v1/spec.json").Should().BeFalse("scheme mismatch");
    }

    [Fact]
    public async Task GetOrFetchAsync_ReturnsNull_OnHttpError()
    {
        var handler = new FakeHttpHandler(statusCode: HttpStatusCode.NotFound);
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var cache = new ConnectedServiceSpecCache(options, http);

        var ops = await cache.GetOrFetchAsync("unknown-service", "svc-unknown", null, "token");

        ops.Should().BeNull();
    }

    [Fact]
    public async Task GetOrFetchAsync_LogsWarning_OnHttpErrorFallback()
    {
        var handler = new FakeHttpHandler(statusCode: HttpStatusCode.NotFound);
        var http = new HttpClient(handler);
        var logger = new RecordingLogger<ConnectedServiceSpecCache>();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var cache = new ConnectedServiceSpecCache(options, http, logger);

        var ops = await cache.GetOrFetchAsync("unknown-service", "svc-unknown", null, "token");

        ops.Should().BeNull();
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("returned 404", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetOrFetchAsync_LogsWarning_OnParseFallback()
    {
        var handler = new FakeHttpHandler("not-json");
        var http = new HttpClient(handler);
        var logger = new RecordingLogger<ConnectedServiceSpecCache>();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var cache = new ConnectedServiceSpecCache(options, http, logger);

        var ops = await cache.GetOrFetchAsync("broken-service", "svc-broken", null, "token");

        ops.Should().BeNull();
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetOrFetchAsync_ReturnsNull_WhenSlugEmpty()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var cache = new ConnectedServiceSpecCache(options);

        var ops = await cache.GetOrFetchAsync("", null, null, "token");
        ops.Should().BeNull();
    }

    [Fact]
    public async Task GetOrFetchAsync_ReturnsNull_WhenNoBaseUrlAndNoSpecUrl()
    {
        var options = new NyxIdToolOptions();
        using var cache = new ConnectedServiceSpecCache(options);

        var ops = await cache.GetOrFetchAsync("github", "svc-github", null, "token");
        ops.Should().BeNull();
    }

    [Fact]
    public async Task GetOrFetchAsync_ReturnsNull_WhenNoSpecUrlAndNoServiceId()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var cache = new ConnectedServiceSpecCache(options);

        var ops = await cache.GetOrFetchAsync("github", null, null, "token");
        ops.Should().BeNull();
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string? _responseBody;
        private readonly HttpStatusCode _statusCode;

        public int RequestCount { get; private set; }
        public string? LastRequestUri { get; private set; }
        public string? LastAuthHeader { get; private set; }

        public FakeHttpHandler(string? responseBody = null, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastAuthHeader = request.Headers.Authorization?.ToString();

            var response = new HttpResponseMessage(_statusCode);
            if (_responseBody is not null)
                response.Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}
