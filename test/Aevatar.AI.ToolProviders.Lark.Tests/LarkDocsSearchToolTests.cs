using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Lark.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.AI.ToolProviders.Lark.Tests;

public sealed class LarkDocsSearchToolTests
{
    [Fact]
    public async Task SearchAsync_ShouldUseSearchV2WithDocAndWikiFilters()
    {
        var (client, handler) = CreateClient("""{"code":0,"data":{"res_units":[]}}""");

        await client.SearchAsync(
            "token-123",
            new LarkKnowledgeSearchRequest("expense policy", 5, []),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().Should().Be(
            "https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/search/v2/doc_wiki/search");
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be("token-123");
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("query").GetString().Should().Be("expense policy");
        body.RootElement.GetProperty("page_size").GetInt32().Should().Be(5);
        body.RootElement.GetProperty("doc_filter").GetProperty("doc_types")
            .EnumerateArray().Select(static item => item.GetString())
            .Should().Equal("DOCX", "WIKI");
        body.RootElement.GetProperty("wiki_filter").GetProperty("doc_types")
            .EnumerateArray().Select(static item => item.GetString())
            .Should().Equal("DOCX", "WIKI");
    }

    [Fact]
    public async Task SearchAsync_WithWikiSpaces_ShouldOnlySendWikiFilter()
    {
        var (client, handler) = CreateClient("""{"code":0,"data":{"res_units":[]}}""");

        await client.SearchAsync(
            "token-123",
            new LarkKnowledgeSearchRequest("runbook", 3, ["space-a", "space-b"]),
            CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.TryGetProperty("doc_filter", out _).Should().BeFalse();
        body.RootElement.GetProperty("wiki_filter").GetProperty("space_ids")
            .EnumerateArray().Select(static item => item.GetString())
            .Should().Equal("space-a", "space-b");
    }

    [Fact]
    public async Task ResolveWikiNodeAsync_ShouldUseEscapedWikiToken()
    {
        var (client, handler) = CreateClient("""{"code":0,"data":{"node":{"obj_type":"docx","obj_token":"doccn_1"}}}""");

        await client.ResolveWikiNodeAsync("token-123", "wikcn/a", CancellationToken.None);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString().Should().Be(
            "https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/wiki/v2/spaces/get_node?token=wikcn%2Fa");
        handler.LastBody.Should().BeNull();
    }

    [Fact]
    public async Task ReadDocxRawContentAsync_ShouldUseEscapedDocumentToken()
    {
        var (client, handler) = CreateClient("""{"code":0,"data":{"content":"policy text"}}""");

        await client.ReadDocxRawContentAsync("token-123", "doccn/a", CancellationToken.None);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString().Should().Be(
            "https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/docx/v1/documents/doccn%2Fa/raw_content");
        handler.LastBody.Should().BeNull();
    }

    [Fact]
    public void ParseSearch_ShouldNormalizeDocxAndWikiCandidates()
    {
        const string payload = """
            {
              "code": 0,
              "data": {
                "has_more": true,
                "page_token": "next-page",
                "res_units": [
                  {
                    "entity_type": "DOCX",
                    "title": "Expense policy",
                    "result_meta": {
                      "token": "doccn_1",
                      "url": "https://example.larksuite.com/docx/doccn_1"
                    }
                  },
                  {
                    "entity_type": "WIKI",
                    "title_highlighted": "<h>Run</h>book &amp; FAQ",
                    "result_meta": {
                      "token": "wikcn_1",
                      "obj_token": "doccn_2",
                      "url": "https://example.larksuite.com/wiki/wikcn_1"
                    }
                  }
                ]
              }
            }
            """;

        var result = LarkKnowledgeResponseParser.ParseSearch(payload);

        result.HasMore.Should().BeTrue();
        result.PageToken.Should().Be("next-page");
        result.Candidates.Should().Equal(
            new LarkKnowledgeCandidate(
                "docx",
                "Expense policy",
                "https://example.larksuite.com/docx/doccn_1",
                "doccn_1",
                "doccn_1"),
            new LarkKnowledgeCandidate(
                "wiki",
                "Runbook & FAQ",
                "https://example.larksuite.com/wiki/wikcn_1",
                "wikcn_1",
                "doccn_2"));
    }

    [Fact]
    public void ParseSearch_ShouldIgnoreUnsupportedOrUnaddressableResults()
    {
        const string payload = """
            {
              "code": 0,
              "data": {
                "res_units": [
                  {"entity_type":"SHEET","title":"Numbers","result_meta":{"token":"shtcn_1"}},
                  {"entity_type":"DOCX","title":"Missing token","result_meta":{}},
                  {"entity_type":"WIKI","title":"Runbook","result_meta":{"token":"wikcn_1"}}
                ]
              }
            }
            """;

        var result = LarkKnowledgeResponseParser.ParseSearch(payload);

        result.Candidates.Should().ContainSingle()
            .Which.Should().Be(new LarkKnowledgeCandidate("wiki", "Runbook", "", "wikcn_1", null));
        result.HasMore.Should().BeFalse();
        result.PageToken.Should().BeNull();
    }

    [Fact]
    public void ParseWikiNode_ShouldReturnUnderlyingDocxIdentity()
    {
        var result = LarkKnowledgeResponseParser.ParseWikiNode(
            """{"code":0,"data":{"node":{"obj_type":"docx","obj_token":"doccn_1"}}}""");

        result.Should().Be(new LarkWikiNodeResult("docx", "doccn_1"));
    }

    [Fact]
    public void ParseDocxRawContent_ShouldReturnContent()
    {
        LarkKnowledgeResponseParser.ParseDocxRawContent(
                """{"code":0,"data":{"content":"Expense limit is 100."}}""")
            .Should()
            .Be("Expense limit is 100.");
    }

    [Theory]
    [InlineData("", "empty_lark_response")]
    [InlineData("not-json", "invalid_lark_response_json")]
    [InlineData("{\"code\":9301,\"data\":{\"msg\":\"blocked\"}}", "lark_code=9301 msg=blocked")]
    [InlineData(
        "{\"error\":true,\"status\":502,\"message\":\"gateway\",\"body\":\"secret provider body\"}",
        "nyx_proxy_error status=502")]
    public void ParseSearch_WithProviderFailure_ShouldThrowSafeError(string payload, string expectedError)
    {
        var act = () => LarkKnowledgeResponseParser.ParseSearch(payload);

        act.Should().Throw<InvalidOperationException>().WithMessage(expectedError);
    }

    [Theory]
    [InlineData("{\"code\":0,\"data\":{\"node\":{\"obj_type\":\"docx\"}}}", "missing_wiki_object_token")]
    [InlineData("{\"code\":0,\"data\":{}}", "missing_docx_content")]
    public void ParseRequiredContent_WhenFieldIsMissing_ShouldThrowStableError(
        string payload,
        string expectedError)
    {
        Action act = expectedError == "missing_wiki_object_token"
            ? () => { _ = LarkKnowledgeResponseParser.ParseWikiNode(payload); }
            : () => { _ = LarkKnowledgeResponseParser.ParseDocxRawContent(payload); };

        act.Should().Throw<InvalidOperationException>().WithMessage(expectedError);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSearchReadAndReturnCitableEvidence()
    {
        var client = new StubKnowledgeClient
        {
            SearchResponse = SearchResponse(
                """{"entity_type":"DOCX","title":"Expense policy","result_meta":{"token":"doccn_1","url":"https://example.larksuite.com/docx/doccn_1"}}"""),
        };
        client.RawContentByToken["doccn_1"] =
            """{"code":0,"data":{"content":"Expense limit is 100."}}""";
        using var _ = new AgentToolRequestMetadataScope("token-123");

        var json = await new LarkDocsSearchTool(client).ExecuteAsync(
            """{"query":"expense policy","max_sources":5}""");

        using var result = JsonDocument.Parse(json);
        var root = result.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("query").GetString().Should().Be("expense policy");
        root.GetProperty("has_more").GetBoolean().Should().BeFalse();
        var source = root.GetProperty("sources")[0];
        source.GetProperty("source_id").GetString().Should().Be("lark:docx:doccn_1");
        source.GetProperty("source_kind").GetString().Should().Be("docx");
        source.GetProperty("title").GetString().Should().Be("Expense policy");
        source.GetProperty("url").GetString().Should().Be(
            "https://example.larksuite.com/docx/doccn_1");
        source.GetProperty("document_token").GetString().Should().Be("doccn_1");
        source.GetProperty("content").GetString().Should().Be("Expense limit is 100.");
        source.GetProperty("content_truncated").GetBoolean().Should().BeFalse();
        root.GetProperty("unreadable_sources").GetArrayLength().Should().Be(0);
        client.LastToken.Should().Be("token-123");
        client.LastSearchRequest.Should().Be(new LarkKnowledgeSearchRequest("expense policy", 5, []));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldResolveWikiNodeBeforeReadingDocx()
    {
        var client = new StubKnowledgeClient
        {
            SearchResponse = SearchResponse(
                """{"entity_type":"WIKI","title":"Runbook","result_meta":{"token":"wikcn_1","url":"https://example.larksuite.com/wiki/wikcn_1"}}"""),
        };
        client.WikiNodeByToken["wikcn_1"] =
            """{"code":0,"data":{"node":{"obj_type":"docx","obj_token":"doccn_2"}}}""";
        client.RawContentByToken["doccn_2"] =
            """{"code":0,"data":{"content":"Escalate to the on-call engineer."}}""";
        using var _ = new AgentToolRequestMetadataScope("token-123");

        var json = await new LarkDocsSearchTool(client).ExecuteAsync("""{"query":"incident runbook"}""");

        using var result = JsonDocument.Parse(json);
        var source = result.RootElement.GetProperty("sources")[0];
        source.GetProperty("source_id").GetString().Should().Be("lark:wiki:wikcn_1");
        source.GetProperty("document_token").GetString().Should().Be("doccn_2");
        client.ResolvedWikiTokens.Should().Equal("wikcn_1");
        client.ReadDocumentTokens.Should().Equal("doccn_2");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDeduplicateAndContinueAfterUnreadableSource()
    {
        var client = new StubKnowledgeClient
        {
            SearchResponse = SearchResponse(
                """{"entity_type":"DOCX","title":"Private policy","result_meta":{"token":"doccn_1"}}""",
                """{"entity_type":"DOCX","title":"Private policy duplicate","result_meta":{"token":"doccn_1"}}""",
                """{"entity_type":"DOCX","title":"Public policy","result_meta":{"token":"doccn_2"}}"""),
        };
        client.RawContentByToken["doccn_1"] = """{"code":999,"msg":"permission denied"}""";
        client.RawContentByToken["doccn_2"] = """{"code":0,"data":{"content":"Readable."}}""";
        using var _ = new AgentToolRequestMetadataScope("token-123");

        var json = await new LarkDocsSearchTool(client).ExecuteAsync("""{"query":"policy"}""");

        using var result = JsonDocument.Parse(json);
        var root = result.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("sources").GetArrayLength().Should().Be(1);
        root.GetProperty("sources")[0].GetProperty("document_token").GetString().Should().Be("doccn_2");
        var unreadable = root.GetProperty("unreadable_sources");
        unreadable.GetArrayLength().Should().Be(1);
        unreadable[0].GetProperty("source_id").GetString().Should().Be("lark:docx:doccn_1");
        unreadable[0].GetProperty("error").GetString().Should().Be("lark_code=999 msg=permission denied");
        client.ReadDocumentTokens.Should().Equal("doccn_1", "doccn_2");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnConnectionFailureWithoutCallingLark()
    {
        var client = new StubKnowledgeClient();
        using var _ = new AgentToolRequestMetadataScope();

        var json = await new LarkDocsSearchTool(client).ExecuteAsync("""{"query":"policy"}""");

        using var result = JsonDocument.Parse(json);
        result.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        result.RootElement.GetProperty("error").GetString().Should().Contain("connect or reauthenticate Lark");
        client.SearchCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectBlankQueryWithoutCallingLark()
    {
        var client = new StubKnowledgeClient();
        using var _ = new AgentToolRequestMetadataScope("token-123");

        var json = await new LarkDocsSearchTool(client).ExecuteAsync("""{"query":"  "}""");

        using var result = JsonDocument.Parse(json);
        result.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        result.RootElement.GetProperty("error").GetString().Should().Be("query is required.");
        client.SearchCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNormalizeQueryAndWikiSpacesBeforeSearch()
    {
        var client = new StubKnowledgeClient();
        using var _ = new AgentToolRequestMetadataScope("token-123");
        var query = new string('x', 29) + "😀😀";

        var json = await new LarkDocsSearchTool(client).ExecuteAsync(JsonSerializer.Serialize(new
        {
            query,
            max_sources = 10,
            space_ids = new[] { " space-a ", "space-a", "space-b" },
        }));

        using var result = JsonDocument.Parse(json);
        var normalizedQuery = new string('x', 29) + "😀";
        result.RootElement.GetProperty("query").GetString().Should().Be(normalizedQuery);
        client.LastSearchRequest.Should().BeEquivalentTo(
            new LarkKnowledgeSearchRequest(normalizedQuery, 10, ["space-a", "space-b"]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task ExecuteAsync_ShouldRejectOutOfRangeMaxSources(int maxSources)
    {
        var client = new StubKnowledgeClient();
        using var _ = new AgentToolRequestMetadataScope("token-123");

        var json = await new LarkDocsSearchTool(client).ExecuteAsync(
            JsonSerializer.Serialize(new { query = "policy", max_sources = maxSources }));

        using var result = JsonDocument.Parse(json);
        result.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        result.RootElement.GetProperty("error").GetString().Should().Be(
            "max_sources must be between 1 and 10.");
        client.SearchCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoMatches_ShouldReturnSuccessfulEmptyEvidence()
    {
        var client = new StubKnowledgeClient();
        using var _ = new AgentToolRequestMetadataScope("token-123");

        var json = await new LarkDocsSearchTool(client).ExecuteAsync("""{"query":"unknown policy"}""");

        using var result = JsonDocument.Parse(json);
        var root = result.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("sources").GetArrayLength().Should().Be(0);
        root.GetProperty("unreadable_sources").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldBoundPerSourceAndTotalEvidenceContent()
    {
        var items = Enumerable.Range(1, 5)
            .Select(index => JsonSerializer.Serialize(new
            {
                entity_type = "DOCX",
                title = $"Doc {index}",
                result_meta = new { token = $"doccn_{index}" },
            }))
            .ToArray();
        var client = new StubKnowledgeClient { SearchResponse = SearchResponse(items) };
        foreach (var index in Enumerable.Range(1, 5))
        {
            client.RawContentByToken[$"doccn_{index}"] = JsonSerializer.Serialize(new
            {
                code = 0,
                data = new { content = new string((char)('a' + index), 12_001) },
            });
        }
        using var _ = new AgentToolRequestMetadataScope("token-123");

        var json = await new LarkDocsSearchTool(client).ExecuteAsync(
            """{"query":"large docs","max_sources":5}""");

        using var result = JsonDocument.Parse(json);
        var sources = result.RootElement.GetProperty("sources").EnumerateArray().ToArray();
        sources.Should().HaveCount(4);
        sources.Sum(source => source.GetProperty("content").GetString()!.Length).Should().Be(48_000);
        sources.Should().OnlyContain(source =>
            source.GetProperty("content").GetString()!.Length == 12_000 &&
            source.GetProperty("content_truncated").GetBoolean());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotSplitUnicodeCharacterAtContentBoundary()
    {
        var client = new StubKnowledgeClient
        {
            SearchResponse = SearchResponse(
                """{"entity_type":"DOCX","title":"Unicode","result_meta":{"token":"doccn_unicode"}}"""),
        };
        client.RawContentByToken["doccn_unicode"] = JsonSerializer.Serialize(new
        {
            code = 0,
            data = new { content = new string('x', 11_999) + "😀tail" },
        });
        using var _ = new AgentToolRequestMetadataScope("token-123");

        var json = await new LarkDocsSearchTool(client).ExecuteAsync("""{"query":"unicode"}""");

        using var result = JsonDocument.Parse(json);
        var source = result.RootElement.GetProperty("sources")[0];
        var content = source.GetProperty("content").GetString()!;
        content.EnumerateRunes().Should().HaveCount(12_000);
        content.Should().EndWith("😀");
        source.GetProperty("content_truncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Tool_ShouldDeclareReadOnlyAutomaticExecution()
    {
        var tool = new LarkDocsSearchTool(new StubKnowledgeClient());

        tool.Name.Should().Be("lark_docs_search");
        tool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);
        tool.IsReadOnly.Should().BeTrue();
        tool.IsDestructive.Should().BeFalse();
        tool.RequiresApproval("""{"query":"policy"}""").Should().BeNull();
    }

    [Fact]
    public async Task KnowledgeToolSourceAndDependencyInjection_ShouldRegisterDocsSearch()
    {
        var client = new StubKnowledgeClient();
        var source = new LarkKnowledgeAgentToolSource(
            new LarkToolOptions(),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            client);

        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle(tool => tool.Name == "lark_docs_search");
        var services = new ServiceCollection();
        services.AddLarkTools();
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(ILarkKnowledgeClient) &&
            descriptor.ImplementationType == typeof(LarkKnowledgeNyxClient));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IAgentToolSource) &&
            descriptor.ImplementationType == typeof(LarkKnowledgeAgentToolSource));
    }

    private static string SearchResponse(params string[] resultUnits) =>
        "{\"code\":0,\"data\":{\"has_more\":false,\"res_units\":[" +
        string.Join(',', resultUnits) +
        "]}}";

    private static (LarkKnowledgeNyxClient Client, RecordingHandler Handler) CreateClient(string response)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json"),
        });
        var client = new LarkKnowledgeNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));
        return (client, handler);
    }

    private sealed class StubKnowledgeClient : ILarkKnowledgeClient
    {
        public string SearchResponse { get; set; } = SearchResponse();
        public Dictionary<string, string> WikiNodeByToken { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> RawContentByToken { get; } = new(StringComparer.Ordinal);
        public string? LastToken { get; private set; }
        public LarkKnowledgeSearchRequest? LastSearchRequest { get; private set; }
        public int SearchCallCount { get; private set; }
        public List<string> ResolvedWikiTokens { get; } = [];
        public List<string> ReadDocumentTokens { get; } = [];

        public Task<string> SearchAsync(
            string token,
            LarkKnowledgeSearchRequest request,
            CancellationToken cancellationToken)
        {
            LastToken = token;
            LastSearchRequest = request;
            SearchCallCount++;
            return Task.FromResult(SearchResponse);
        }

        public Task<string> ResolveWikiNodeAsync(
            string token,
            string wikiToken,
            CancellationToken cancellationToken)
        {
            ResolvedWikiTokens.Add(wikiToken);
            return Task.FromResult(WikiNodeByToken[wikiToken]);
        }

        public Task<string> ReadDocxRawContentAsync(
            string token,
            string documentToken,
            CancellationToken cancellationToken)
        {
            ReadDocumentTokens.Add(documentToken);
            return Task.FromResult(RawContentByToken[documentToken]);
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }

    private sealed class AgentToolRequestMetadataScope : IDisposable
    {
        private readonly AgentToolExecutionContext? _previous = AgentToolRequestContext.Current;

        public AgentToolRequestMetadataScope(string? accessToken = null)
        {
            AgentToolRequestContext.Current = string.IsNullOrWhiteSpace(accessToken)
                ? null
                : global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
                {
                    [LLMRequestMetadataKeys.NyxIdAccessToken] = accessToken,
                });
        }

        public void Dispose() => AgentToolRequestContext.Current = _previous;
    }
}
