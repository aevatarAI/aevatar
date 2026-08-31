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
