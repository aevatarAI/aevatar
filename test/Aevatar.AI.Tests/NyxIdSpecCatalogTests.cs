using System.Net;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class NyxIdSpecCatalogTests
{
    [Fact]
    public void Constructor_NoBaseUrl_DoesNotFetch()
    {
        var handler = new FakeHttpHandler();
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = null, SpecFetchToken = "ignored" };

        using var catalog = new NyxIdSpecCatalog(options, http);

        handler.RequestCount.Should().Be(0);
        catalog.Operations.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_BaseUrlWithoutSpecFetchToken_SkipsBackgroundRefresh()
    {
        // Regression guard: NyxID's /api/v1/docs/openapi.json is human-only and
        // returns 401 without a real user's API key. A configured BaseUrl alone
        // must not trigger a fetch — otherwise prod logs fill with 30-min 401s.
        var handler = new FakeHttpHandler();
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test", SpecFetchToken = null };

        using var catalog = new NyxIdSpecCatalog(options, http);

        handler.RequestCount.Should().Be(0);
        catalog.Operations.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WhitespaceSpecFetchToken_TreatedAsMissing()
    {
        var handler = new FakeHttpHandler();
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test", SpecFetchToken = "   " };

        using var catalog = new NyxIdSpecCatalog(options, http);

        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Constructor_BaseUrlAndSpecFetchToken_FetchesWithBearer()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "paths": {
                "/things": {
                  "get": { "operationId": "list_things", "summary": "List things" }
                }
              }
            }
            """;
        var handler = new FakeHttpHandler(spec);
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions
        {
            BaseUrl = "https://nyx.test",
            SpecFetchToken = "user-api-key-xyz",
        };

        using var catalog = new NyxIdSpecCatalog(options, http);

        await handler.FirstRequestReceived.Task;

        handler.LastRequestUri.Should().Be("https://nyx.test/api/v1/docs/openapi.json");
        handler.LastAuthHeader.Should().Be("Bearer user-api-key-xyz");
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string? _responseBody;
        private readonly HttpStatusCode _statusCode;

        public int RequestCount { get; private set; }
        public string? LastRequestUri { get; private set; }
        public string? LastAuthHeader { get; private set; }
        public TaskCompletionSource<bool> FirstRequestReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            FirstRequestReceived.TrySetResult(true);

            var response = new HttpResponseMessage(_statusCode);
            if (_responseBody is not null)
                response.Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }
}
