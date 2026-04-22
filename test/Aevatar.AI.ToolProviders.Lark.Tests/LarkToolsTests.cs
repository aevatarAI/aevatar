using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Lark.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.ToolProviders.Lark.Tests;

public class LarkToolsTests
{
    [Fact]
    public async Task LarkMessagesSendTool_SendsTextMessage_AndNormalizesResponse()
    {
        var client = new StubLarkNyxClient
        {
            SendResponse = """{"code":0,"data":{"message_id":"om_123","chat_id":"oc_456","create_time":"1730000000"}}""",
        };
        var tool = new LarkMessagesSendTool(client);
        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-123",
        };

        try
        {
            var result = await tool.ExecuteAsync(
                """{"target_type":"chat_id","target_id":"oc_456","message_type":"text","text":"Hello from Aevatar"}""");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            document.RootElement.GetProperty("message_id").GetString().Should().Be("om_123");
            document.RootElement.GetProperty("target_type").GetString().Should().Be("chat_id");
            client.LastSendRequest.Should().NotBeNull();
            client.LastSendRequest!.MessageType.Should().Be("text");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task LarkMessagesSendTool_ValidatesInteractiveCardJson()
    {
        var tool = new LarkMessagesSendTool(new StubLarkNyxClient());
        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-123",
        };

        try
        {
            var result = await tool.ExecuteAsync(
                """{"target_type":"chat_id","target_id":"oc_456","message_type":"interactive_card","card_json":"{bad json}"}""");

            result.Should().Contain("card_json is not valid JSON");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task LarkChatsLookupTool_ReturnsNormalizedCandidates()
    {
        var client = new StubLarkNyxClient
        {
            SearchResponse =
                """
                {
                  "code": 0,
                  "data": {
                    "items": [
                      { "meta_data": { "chat_id": "oc_2", "name": "Beta", "chat_mode": "group", "chat_status": "normal" } },
                      { "meta_data": { "chat_id": "oc_1", "name": "Alpha", "chat_mode": "group", "chat_status": "normal" } }
                    ],
                    "total": 2,
                    "has_more": false
                  }
                }
                """,
        };
        var tool = new LarkChatsLookupTool(client);
        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-123",
        };

        try
        {
            var result = await tool.ExecuteAsync("""{"query":"Alpha","exact_match_hint":true}""");

            using var document = JsonDocument.Parse(result);
            var chats = document.RootElement.GetProperty("chats");
            chats.GetArrayLength().Should().Be(2);
            chats[0].GetProperty("chat_id").GetString().Should().Be("oc_1");
            chats[0].GetProperty("exact_name_match").GetBoolean().Should().BeTrue();
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task LarkChatsLookupTool_RequiresQueryOrMemberIds()
    {
        var tool = new LarkChatsLookupTool(new StubLarkNyxClient());
        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-123",
        };

        try
        {
            var result = await tool.ExecuteAsync("""{}""");
            result.Should().Contain("At least one of query or member_ids is required");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task LarkAgentToolSource_RegistersTools_WhenNyxConfigured()
    {
        var source = new LarkAgentToolSource(
            new LarkToolOptions(),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new StubLarkNyxClient());

        var tools = await source.DiscoverToolsAsync();

        tools.Should().HaveCount(2);
        tools.Should().Contain(tool => tool is LarkMessagesSendTool);
        tools.Should().Contain(tool => tool is LarkChatsLookupTool);
    }

    [Fact]
    public async Task LarkAgentToolSource_SkipsTools_WhenNyxBaseUrlMissing()
    {
        var source = new LarkAgentToolSource(
            new LarkToolOptions(),
            new NyxIdToolOptions(),
            new StubLarkNyxClient());

        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task LarkNyxClient_SendMessage_ShapesProxyRequest()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"message_id":"om_1"}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.SendMessageAsync(
            "token-123",
            new LarkSendMessageRequest("chat_id", "oc_123", "text", """{"text":"Hello"}""", "uuid-1"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages?receive_id_type=chat_id");
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be("token-123");

        var body = handler.LastBody;
        body.Should().Contain("receive_id");
        body.Should().Contain("uuid-1");
    }

    [Fact]
    public async Task LarkNyxClient_SearchChats_ShapesProxyRequest()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"items":[],"total":0}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.SearchChatsAsync(
            "token-123",
            new LarkChatSearchRequest("team-alpha", ["ou_1"], ["public_joined"], true, false, 10, "page-1"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v2/chats/search?page_size=10&page_token=page-1");

        var body = handler.LastBody;
        body.Should().Contain("\"query\":\"\\u0022team-alpha\\u0022\"");
        body.Should().Contain("member_ids");
        body.Should().Contain("search_types");
    }

    private sealed class StubLarkNyxClient : ILarkNyxClient
    {
        public string SendResponse { get; set; } = """{"code":0,"data":{}}""";
        public string SearchResponse { get; set; } = """{"code":0,"data":{"items":[],"total":0}}""";

        public LarkSendMessageRequest? LastSendRequest { get; private set; }
        public LarkChatSearchRequest? LastSearchRequest { get; private set; }

        public Task<string> SendMessageAsync(string token, LarkSendMessageRequest request, CancellationToken ct)
        {
            LastSendRequest = request;
            return Task.FromResult(SendResponse);
        }

        public Task<string> SearchChatsAsync(string token, LarkChatSearchRequest request, CancellationToken ct)
        {
            LastSearchRequest = request;
            return Task.FromResult(SearchResponse);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }
}
