using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
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
}
