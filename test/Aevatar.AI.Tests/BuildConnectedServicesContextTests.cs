using System.Net;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class BuildConnectedServicesContextTests
{
    private const string ServicesJson = """
        {
          "services": [
            {
              "slug": "api-github",
              "name": "GitHub",
              "endpoint_url": "https://api.github.com",
              "openapi_url": "https://nyx.test/proxy/github/openapi.json"
            },
            {
              "slug": "custom-svc",
              "name": "Custom Service",
              "endpoint_url": "https://custom.test"
            }
          ]
        }
        """;

    private const string GithubSpec = """
        {
          "paths": {
            "/repos": {
              "get": { "operationId": "list_repos", "summary": "List repositories" }
            },
            "/repos/{owner}/{repo}/issues": {
              "get": { "operationId": "list_issues", "summary": "List issues" },
              "post": { "operationId": "create_issue", "summary": "Create issue" }
            }
          }
        }
        """;

    [Fact]
    public async Task BuildContext_WithSpecCache_UsesSpecDerivenHints()
    {
        var handler = new FakeHttpHandler(GithubSpec);
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var specCache = new ConnectedServiceSpecCache(options, http);

        var context = await NyxIdChatEndpoints.BuildConnectedServicesContextAsync(
            ServicesJson, specCache, "token", CancellationToken.None);

        context.Should().Contain("<connected-services>");
        context.Should().Contain("GitHub");
        context.Should().Contain("slug: `api-github`");
        context.Should().Contain("<api-hints>");
        context.Should().Contain("from spec");
        context.Should().Contain("GET /repos — List repositories");
    }

    [Fact]
    public async Task BuildContext_FallsBackToHardcoded_WhenSpecUnavailable()
    {
        var handler = new FakeHttpHandler(statusCode: HttpStatusCode.NotFound);
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var specCache = new ConnectedServiceSpecCache(options, http);

        var servicesJson = """
            {
              "services": [
                { "slug": "bot-telegram-mybot", "name": "My Telegram Bot" }
              ]
            }
            """;

        var context = await NyxIdChatEndpoints.BuildConnectedServicesContextAsync(
            servicesJson, specCache, "token", CancellationToken.None);

        context.Should().Contain("Telegram Bot");
        context.Should().Contain("<api-hints>");
    }

    [Fact]
    public async Task BuildContext_WithoutSpecCache_UsesHardcodedHints()
    {
        var servicesJson = """
            {
              "services": [
                { "slug": "api-github", "name": "GitHub", "endpoint_url": "https://api.github.com" }
              ]
            }
            """;

        var context = await NyxIdChatEndpoints.BuildConnectedServicesContextAsync(
            servicesJson, null, "", CancellationToken.None);

        context.Should().Contain("GitHub API");
        context.Should().Contain("<api-hints>");
    }

    [Fact]
    public async Task BuildContext_EmptyServices_ShowsNoServicesMessage()
    {
        var context = await NyxIdChatEndpoints.BuildConnectedServicesContextAsync(
            """{ "services": [] }""", null, "", CancellationToken.None);

        context.Should().Contain("No services connected yet");
    }

    [Fact]
    public async Task BuildContext_UsesExplicitOpenapiUrl()
    {
        var handler = new FakeHttpHandler(GithubSpec);
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var specCache = new ConnectedServiceSpecCache(options, http);

        var singleServiceJson = """
            {
              "services": [
                {
                  "slug": "api-github",
                  "name": "GitHub",
                  "openapi_url": "https://custom.test/github-spec.json"
                }
              ]
            }
            """;

        await NyxIdChatEndpoints.BuildConnectedServicesContextAsync(
            singleServiceJson, specCache, "token", CancellationToken.None);

        handler.LastRequestUri.Should().Be("https://custom.test/github-spec.json");
    }

    [Fact]
    public async Task BuildContext_ConstructsSpecUrl_WhenNoOpenapiUrl()
    {
        var handler = new FakeHttpHandler(GithubSpec);
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var specCache = new ConnectedServiceSpecCache(options, http);

        var singleServiceJson = """
            {
              "services": [
                { "slug": "my-api", "name": "My API" }
              ]
            }
            """;

        await NyxIdChatEndpoints.BuildConnectedServicesContextAsync(
            singleServiceJson, specCache, "token", CancellationToken.None);

        handler.LastRequestUri.Should().Be("https://nyx.test/api/v1/proxy/services/my-api/openapi.json");
    }

    [Fact]
    public async Task BuildContext_DeduplicatesHardcodedHints_ForSamePattern()
    {
        var handler = new FakeHttpHandler(statusCode: HttpStatusCode.NotFound);
        var http = new HttpClient(handler);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var specCache = new ConnectedServiceSpecCache(options, http);

        var servicesJson = """
            {
              "services": [
                { "slug": "bot-telegram-one", "name": "Telegram Bot 1" },
                { "slug": "bot-telegram-two", "name": "Telegram Bot 2" }
              ]
            }
            """;

        var context = await NyxIdChatEndpoints.BuildConnectedServicesContextAsync(
            servicesJson, specCache, "token", CancellationToken.None);

        var count = context.Split("### Telegram Bot").Length - 1;
        count.Should().Be(1, "duplicate hints for same pattern should be deduplicated");
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string? _responseBody;
        private readonly HttpStatusCode _statusCode;

        public string? LastRequestUri { get; private set; }

        public FakeHttpHandler(string? responseBody = null, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri?.ToString();
            var response = new HttpResponseMessage(_statusCode);
            if (_responseBody is not null)
                response.Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }
}
