using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Unit tests for NyxIdProxyTool dual-token routing helpers:
/// ParseServiceSlugs and NyxID error envelope detection.
/// </summary>
public class NyxIdProxyToolDualTokenTests
{
    // ─── ParseServiceSlugs ───

    [Fact]
    public void ParseServiceSlugs_ArrayOfServices_ExtractsSlugs()
    {
        var json = """[{"slug":"api-github","name":"GitHub"},{"slug":"llm-openai","name":"OpenAI"}]""";
        using var doc = JsonDocument.Parse(json);

        var slugs = NyxIdProxyTool.ParseServiceSlugs(doc);

        slugs.Should().HaveCount(2);
        slugs.Should().Contain("api-github");
        slugs.Should().Contain("llm-openai");
    }

    [Fact]
    public void ParseServiceSlugs_EmptyArray_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("[]");

        var slugs = NyxIdProxyTool.ParseServiceSlugs(doc);

        slugs.Should().BeEmpty();
    }

    [Fact]
    public void ParseServiceSlugs_PaginatedProxyResponse_ExtractsBothServiceGroups()
    {
        using var doc = JsonDocument.Parse("""
            {
              "services":[{"slug":"api-github"}],
              "custom_services":[{"slug":"api-lark-bot"}],
              "total":2,
              "page":1,
              "per_page":100
            }
            """);

        var slugs = NyxIdProxyTool.ParseServiceSlugs(doc);

        slugs.Should().Contain("api-github");
        slugs.Should().Contain("api-lark-bot");
    }

    [Fact]
    public void ParseServiceSlugs_NonArrayRoot_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{"error":"unauthorized"}""");

        var slugs = NyxIdProxyTool.ParseServiceSlugs(doc);

        slugs.Should().BeEmpty();
    }

    [Fact]
    public void ParseServiceSlugs_MissingSlugField_SkipsEntry()
    {
        var json = """[{"slug":"api-github"},{"name":"no-slug-here"},{"slug":"llm-openai"}]""";
        using var doc = JsonDocument.Parse(json);

        var slugs = NyxIdProxyTool.ParseServiceSlugs(doc);

        slugs.Should().HaveCount(2);
        slugs.Should().Contain("api-github");
        slugs.Should().Contain("llm-openai");
    }

    [Fact]
    public void ParseServiceSlugs_CaseInsensitive()
    {
        var json = """[{"slug":"Api-GitHub"}]""";
        using var doc = JsonDocument.Parse(json);

        var slugs = NyxIdProxyTool.ParseServiceSlugs(doc);

        slugs.Should().Contain("api-github");
        slugs.Should().Contain("API-GITHUB");
    }

    [Fact]
    public async Task ProxyExecution_UsesLiveNyxIdDiscoveryForEachRouteDecision()
    {
        var handler = new RecordingNyxIdHandler();
        using var http = new HttpClient(handler);
        var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, http);
        var tool = new NyxIdProxyTool(client);

        using var _ = AgentToolContextScope.Push(new AgentToolExecutionContext(
            AgentToolRequestIdentity.Empty,
            new AgentToolCredentials("user-token", "org-token", null),
            AgentToolCallerContext.Empty,
            AgentToolChannelContext.Empty,
            AgentToolSenderBindingContext.Empty,
            LLMRequestRoutingContext.Empty,
            AgentToolConnectedServicesContext.Empty,
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal)));

        var first = await tool.ExecuteAsync("""{"slug":"org-service","path":"/ping"}""");
        var second = await tool.ExecuteAsync("""{"slug":"org-service","path":"/ping"}""");

        first.Should().Be("""{"ok":true,"token":"org-token"}""");
        second.Should().Be("""{"ok":true,"token":"org-token"}""");
        handler.ServiceDiscoveryRequests.Should().Be(4, "routing must read live NyxID service discovery instead of keeping slug facts in a tool-instance cache");
        handler.ProxyRequests.Should().Be(2);
    }

    // ─── LooksLikeErrorEnvelope ───

    [Theory]
    [InlineData("""{"error":true,"status":401,"body":""}""")]
    [InlineData("""{"error":"unauthorized"}""")]
    public void LooksLikeErrorEnvelope_TruthyError_True(string input)
    {
        // Used by DiscoverMergedServicesAsync to short-circuit when both user and org
        // discovery returned NyxID error envelopes; without it, the merge synthesizes
        // an empty array and downstream tool-result classification misclassifies the run
        // as successful (PR #471 review round 2).
        NyxIdProxyTool.LooksLikeErrorEnvelope(input).Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"error":false,"data":{}}""")]
    [InlineData("""{"error":null}""")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""[{"slug":"api-github"}]""")]
    [InlineData("plain text")]
    [InlineData("")]
    public void LooksLikeErrorEnvelope_NotAnEnvelope_False(string input)
    {
        NyxIdProxyTool.LooksLikeErrorEnvelope(input).Should().BeFalse();
    }

    private sealed class RecordingNyxIdHandler : HttpMessageHandler
    {
        public int ServiceDiscoveryRequests { get; private set; }
        public int ProxyRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path == "/api/v1/proxy/services")
            {
                ServiceDiscoveryRequests++;
                var body = token == "user-token"
                    ? """[{"slug":"user-service"}]"""
                    : """[{"slug":"org-service"}]""";
                return Task.FromResult(Json(body));
            }

            if (path == "/api/v1/proxy/s/org-service/ping")
            {
                ProxyRequests++;
                return Task.FromResult(Json($$"""{"ok":true,"token":"{{token}}"}"""));
            }

            return Task.FromResult(Json($$"""{"error":"unexpected_request","path":"{{path}}"}"""));
        }

        private static HttpResponseMessage Json(string body) => new()
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
