using System.Net;
using System.Text.Json;
using Aevatar.AI.ToolProviders.ChronoStorage;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Web;
using FluentAssertions;

namespace Aevatar.AI.Tests;

// Test-add (test-coverage/pr-678/cluster-019):
//   Covers refactor-introduced behavior in IHttpClientFactory-backed tool provider clients.
//   Cluster intent: Tool-provider HTTP clients use factory-owned clients and only dispose self-created fallbacks.
public sealed class ToolProviderHttpClientOwnershipTests
{
    [Fact]
    public async Task NyxIdApiClient_Dispose_ShouldNotDisposeInjectedHttpClient()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}"""),
        });
        using var http = new HttpClient(handler);
        var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, http);

        client.Dispose();
        var result = await client.GetCurrentUserAsync("token-1", CancellationToken.None);

        result.Should().Be("""{"ok":true}""");
        handler.Requests.Should().ContainSingle().Which.Headers.Authorization!.Parameter.Should().Be("token-1");
    }

    [Fact]
    public async Task WebApiClient_Dispose_ShouldNotDisposeInjectedHttpClient()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"results":[{"title":"Agent tools","url":"https://example.com/agent-tools","snippet":"docs"}]}"""),
        });
        using var http = new HttpClient(handler);
        var client = new WebApiClient(
            new WebToolOptions
            {
                SearchApiBaseUrl = "https://search.test",
                FetchTimeoutSeconds = 10,
            },
            http);

        client.Dispose();
        var result = await client.SearchAsync("token-1", "agent tools", 3, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Results.Should().ContainSingle().Which.Should().Be(
            new WebSearchResultItem("Agent tools", "https://example.com/agent-tools", "docs"));
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.RequestUri!.AbsoluteUri.Should().Be("https://search.test/search?q=agent%20tools&limit=3");
        request.Headers.Authorization!.Parameter.Should().Be("token-1");
    }

    [Fact]
    public async Task WebApiClient_SearchAsync_ShouldReturnTypedError_WhenNoBackendConfigured()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"results":[]}"""),
        });
        using var http = new HttpClient(handler);
        var client = new WebApiClient(new WebToolOptions(), http);

        var result = await client.SearchAsync("token-1", "agent tools", 3, CancellationToken.None);

        result.Results.Should().BeEmpty();
        result.Error.Should().Be(new WebToolError(
            "search_backend_not_configured",
            "No search backend configured. Set NyxIdSearchSlug or SearchApiBaseUrl in WebToolOptions."));
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task WebApiClient_SearchAsync_ShouldMapEmptyBodyToEmptyTypedResult()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty),
        });
        using var http = new HttpClient(handler);
        var client = new WebApiClient(
            new WebToolOptions
            {
                SearchApiBaseUrl = "https://search.test",
            },
            http);

        var result = await client.SearchAsync("token-1", "agent tools", 3, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Results.Should().BeEmpty();
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.RequestUri!.AbsoluteUri.Should().Be("https://search.test/search?q=agent%20tools&limit=3");
        request.Headers.Authorization!.Parameter.Should().Be("token-1");
    }

    [Fact]
    public async Task WebApiClient_SearchAsync_ShouldMapNonJsonBodyToTypedError()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("plain text result"),
        });
        using var http = new HttpClient(handler);
        var client = new WebApiClient(
            new WebToolOptions
            {
                SearchApiBaseUrl = "https://search.test",
            },
            http);

        var result = await client.SearchAsync("token-1", "agent tools", 3, CancellationToken.None);

        result.Results.Should().BeEmpty();
        result.Error.Should().Be(new WebToolError("unstructured_search_result", "plain text result"));
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.RequestUri!.AbsoluteUri.Should().Be("https://search.test/search?q=agent%20tools&limit=3");
        request.Headers.Authorization!.Parameter.Should().Be("token-1");
    }

    [Fact]
    public async Task WebApiClient_SearchAsync_ShouldMapHttpHandlerExceptionToTypedError()
    {
        var handler = new RecordingHttpMessageHandler(_ =>
            throw new HttpRequestException("network unavailable"));
        using var http = new HttpClient(handler);
        var client = new WebApiClient(
            new WebToolOptions
            {
                SearchApiBaseUrl = "https://search.test",
            },
            http);

        var result = await client.SearchAsync("token-1", "agent tools", 3, CancellationToken.None);

        result.Results.Should().BeEmpty();
        result.Error.Should().Be(new WebToolError("request_failed", "network unavailable"));
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.RequestUri!.AbsoluteUri.Should().Be("https://search.test/search?q=agent%20tools&limit=3");
        request.Headers.Authorization!.Parameter.Should().Be("token-1");
    }

    [Fact]
    public async Task ChronoStorageApiClient_Dispose_ShouldNotDisposeInjectedHttpClient()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"files":[]}"""),
        });
        using var http = new HttpClient(handler);
        var client = new ChronoStorageApiClient(
            new ChronoStorageToolOptions { ApiBaseUrl = "https://storage.test" },
            http);

        client.Dispose();
        var result = await client.GetManifestAsync("token-1", CancellationToken.None);

        result.Should().Be("""{"files":[]}""");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.RequestUri!.ToString().Should().Be("https://storage.test/api/explorer/manifest");
        request.Headers.Authorization!.Parameter.Should().Be("token-1");
    }

    [Fact]
    public async Task ConnectedServiceSpecCache_ShouldUseNamedFactoryClientAndAvoidTokenOnUntrustedHost()
    {
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildOpenApiJson()),
            });
        var factory = new RecordingHttpClientFactory(name => new HttpClient(handler));
        var cache = new ConnectedServiceSpecCache(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            factory);

        var operations = await cache.GetOrFetchAsync(
            "github",
            serviceId: null,
            specUrl: "https://specs.test/openapi.json",
            accessToken: "secret-token",
            ct: CancellationToken.None);

        operations.Should().ContainSingle().Which.OperationId.Should().Be("listRepos");
        factory.CreatedNames.Should().ContainSingle().Which.Should().Be(ConnectedServiceSpecCache.HttpClientName);
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.RequestUri!.ToString().Should().Be("https://specs.test/openapi.json");
        request.Headers.Authorization.Should().BeNull("NyxID bearer tokens must not be sent to third-party spec hosts");
    }

    [Fact]
    public async Task ConnectedServiceSpecCache_ShouldUseNamedFactoryClientForEachLiveFetch()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildOpenApiJson()),
        });
        var factory = new RecordingHttpClientFactory(name => new HttpClient(handler));
        var cache = new ConnectedServiceSpecCache(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            factory);

        var first = await cache.GetOrFetchAsync("github", "svc-1", null, "secret-token", CancellationToken.None);
        var second = await cache.GetOrFetchAsync("github", "svc-1", null, "secret-token", CancellationToken.None);

        first.Should().BeEquivalentTo(second);
        first.Should().ContainSingle().Which.Path.Should().Be("/repos");
        factory.CreatedNames.Should().HaveCount(2)
            .And.OnlyContain(name => name == ConnectedServiceSpecCache.HttpClientName);
        handler.Requests.Should().HaveCount(2, "connected-service spec hints must not keep process-local OpenAPI snapshots");
        handler.Requests[0].RequestUri!.ToString().Should().Be("https://nyx.test/api/v1/proxy/services/svc-1/openapi.json");
        handler.Requests[0].Headers.Authorization!.Parameter.Should().Be("secret-token");
        handler.Requests[1].RequestUri!.ToString().Should().Be("https://nyx.test/api/v1/proxy/services/svc-1/openapi.json");
        handler.Requests[1].Headers.Authorization!.Parameter.Should().Be("secret-token");
    }

    private static string BuildOpenApiJson() =>
        JsonSerializer.Serialize(new
        {
            paths = new Dictionary<string, object>
            {
                ["/repos"] = new
                {
                    get = new
                    {
                        operationId = "listRepos",
                        summary = "List repositories",
                    },
                },
            },
        });

    private sealed class RecordingHttpClientFactory(Func<string, HttpClient> createClient) : IHttpClientFactory
    {
        public List<string> CreatedNames { get; } = [];

        public HttpClient CreateClient(string name)
        {
            CreatedNames.Add(name);
            return createClient(name);
        }
    }

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(CloneRequest(request));
            return Task.FromResult(respond(request));
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return clone;
        }
    }
}
