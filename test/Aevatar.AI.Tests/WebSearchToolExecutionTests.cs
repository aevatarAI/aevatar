using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.AI.ToolProviders.Web.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class WebSearchToolExecutionTests
{
    [Fact]
    public async Task DiscoverToolsAsync_WhenSearchBackendIsNotConfigured_ShouldExposeSearchWithTypedBlocker()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"results":[]}"""),
        });
        using var http = new HttpClient(handler);
        var options = new WebToolOptions();
        using var client = new WebApiClient(options, http);
        var source = new WebAgentToolSource(options, client);
        using var _ = AgentToolContextScope.Push(WithNyxIdAccessToken("token-1"));

        var tools = await source.DiscoverToolsAsync();
        var search = tools.Should().ContainSingle(tool => tool.Name == "web_search").Subject;
        var result = await search.ExecuteAsync("""{"query":"official X API documentation"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString()
            .Should().Be("search_backend_not_configured");
        document.RootElement.GetProperty("message").GetString()
            .Should().Contain("No search backend configured");
        var receipt = search.CreateResultReceipt("call-search", search.Name, "{}", result);
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("search_backend_not_configured");
        receipt.ResultJson.Should().Be(result);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ObjectPayload_ReturnsExpectedJson()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"results":[{"title":"Aevatar docs","url":"https://docs.example/aevatar","snippet":"typed mapper"}],"count":1}"""),
        });
        using var http = new HttpClient(handler);
        var sut = CreateTool(http);
        using var contextScope = AgentToolContextScope.Push(WithNyxIdAccessToken("token-1"));

        var result = await sut.ExecuteAsync("""{"query":"aevatar docs","max_results":3}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.TryGetProperty("count", out _).Should().BeFalse();
        var item = root.GetProperty("results")[0];
        item.GetProperty("title").GetString().Should().Be("Aevatar docs");
        item.GetProperty("url").GetString().Should().Be("https://docs.example/aevatar");
        item.GetProperty("snippet").GetString().Should().Be("typed mapper");
        var receipt = sut.CreateResultReceipt("call-search", sut.Name, "{}", result);
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.ResultJson.Should().Be(result);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.RequestUri!.AbsoluteUri.Should().Be("https://search.test/search?q=aevatar%20docs&limit=3");
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization!.Parameter.Should().Be("token-1");
    }

    [Fact]
    public async Task ExecuteAsync_NonJsonStringPayload_ReturnsTypedErrorJson()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("plain text result"),
        });
        using var http = new HttpClient(handler);
        var sut = CreateTool(http);
        using var _ = AgentToolContextScope.Push(WithNyxIdAccessToken("token-2"));

        var result = await sut.ExecuteAsync("""{"query":"aevatar docs"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString().Should().Be("unstructured_search_result");
        document.RootElement.GetProperty("message").GetString().Should().Be("plain text result");

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.RequestUri!.AbsoluteUri.Should().Be("https://search.test/search?q=aevatar%20docs&limit=9");
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization!.Parameter.Should().Be("token-2");
    }

    [Theory]
    [InlineData("\"plain json string\"", "plain json string")]
    [InlineData("[1,2,3]", "[1,2,3]")]
    public void ParseSearchPayload_WhenJsonRootIsNotObject_ShouldReturnUnstructuredSearchResult(
        string payload,
        string expectedMessage)
    {
        var result = WebToolResultBoundaryJson.ParseSearchPayload(payload);

        result.Results.Should().BeEmpty();
        result.Error.Should().Be(new WebToolError("unstructured_search_result", expectedMessage));
    }

    [Fact]
    public async Task SearchAsync_ShouldRoundTripProviderPayloadThroughTypedDtoAndBoundaryJson()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"results":[{"title":"fresh","url":"https://example.com/fresh","snippet":"fresh snippet","rank":1}]}"""),
        });
        using var http = new HttpClient(handler);
        var client = new WebApiClient(
            new WebToolOptions
            {
                SearchApiBaseUrl = "https://search.test",
            },
            http);

        var result = await client.SearchAsync("token-3", "fresh docs", 4, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Results.Should().ContainSingle().Which.Should().Be(
            new WebSearchResultItem("fresh", "https://example.com/fresh", "fresh snippet"));

        using var document = JsonDocument.Parse(WebToolResultBoundaryJson.ToBoundaryJson(result));
        var root = document.RootElement;
        root.TryGetProperty("rank", out _).Should().BeFalse();
        var item = root.GetProperty("results")[0];
        item.GetProperty("title").GetString().Should().Be("fresh");
        item.GetProperty("url").GetString().Should().Be("https://example.com/fresh");
        item.GetProperty("snippet").GetString().Should().Be("fresh snippet");
    }

    [Fact]
    public async Task SearchAsync_ShouldMapProviderErrorJsonThroughTypedDtoAndBoundaryJson()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"error":"request_failed","message":"boom"}"""),
        });
        using var http = new HttpClient(handler);
        var client = new WebApiClient(
            new WebToolOptions
            {
                SearchApiBaseUrl = "https://search.test",
            },
            http);

        var result = await client.SearchAsync("token-4", "broken backend", 2, CancellationToken.None);

        result.Results.Should().BeEmpty();
        result.Error.Should().Be(new WebToolError("request_failed", "boom"));

        using var document = JsonDocument.Parse(WebToolResultBoundaryJson.ToBoundaryJson(result));
        var root = document.RootElement;
        root.GetProperty("error").GetString().Should().Be("request_failed");
        root.GetProperty("message").GetString().Should().Be("boom");

        handler.Requests.Should().ContainSingle()
            .Which.RequestUri!.AbsoluteUri.Should().Be(
                "https://search.test/search?q=broken%20backend&limit=2");
    }

    [Fact]
    public async Task SearchAsync_WithFirecrawlNyxIdSlug_ShouldUseNyxIdSlugProxyAndMapFirecrawlWebResults()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "success": true,
                  "data": {
                    "web": [
                      {
                        "title": "Lark send message",
                        "url": "https://open.larksuite.com/document/server-docs/im-v1/message/create",
                        "description": "Send messages by chat_id."
                      }
                    ]
                  }
                }
                """),
        });
        using var http = new HttpClient(handler);
        var client = new WebApiClient(
            new WebToolOptions
            {
                NyxIdBaseUrl = "https://nyxid.example.test",
                NyxIdSearchSlug = "api-firecrawl",
            },
            http);

        var result = await client.SearchAsync("token-5", "lark send message docs", 3, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Results.Should().ContainSingle().Which.Should().Be(
            new WebSearchResultItem(
                "Lark send message",
                "https://open.larksuite.com/document/server-docs/im-v1/message/create",
                "Send messages by chat_id."));

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsoluteUri.Should().Be(
            "https://nyxid.example.test/api/v1/proxy/s/api-firecrawl/v2/search");
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization!.Parameter.Should().Be("token-5");
        request.Content.Should().NotBeNull();
        var body = await request.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("query").GetString().Should().Be("lark send message docs");
        root.GetProperty("limit").GetInt32().Should().Be(3);
    }

    private static WebSearchTool CreateTool(HttpClient http)
    {
        var options = new WebToolOptions
        {
            SearchApiBaseUrl = "https://search.test",
            MaxSearchResults = 9,
        };
        return new WebSearchTool(new WebApiClient(options, http), options);
    }

    private static AgentToolExecutionContext WithNyxIdAccessToken(string accessToken) =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdAccessToken = accessToken,
            },
        };

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(await CloneRequestAsync(request, cancellationToken));
            return respond(request);
        }

        private static async Task<HttpRequestMessage> CloneRequestAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (request.Content != null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                clone.Content = new StringContent(body);
                foreach (var header in request.Content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }
}
