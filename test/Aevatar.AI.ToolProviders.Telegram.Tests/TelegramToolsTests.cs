using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Telegram;
using Aevatar.AI.ToolProviders.Telegram.Tools;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.ToolProviders.Telegram.Tests;

public class TelegramToolsTests
{
    [Fact]
    public async Task SendMessage_returns_success_with_message_id_and_chat_id()
    {
        var client = new StubTelegramNyxClient
        {
            SendResponse = """{"ok":true,"result":{"message_id":42,"chat":{"id":12345,"type":"private"},"date":1730000000}}""",
        };
        var tool = new TelegramMessagesSendTool(client);

        using var _ = new AgentToolRequestMetadataScope("token-abc");
        var result = await tool.ExecuteAsync(
            """{"chat_id":"12345","text":"hello world"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("message_id").GetInt32().Should().Be(42);
        document.RootElement.GetProperty("chat_id").GetString().Should().Be("12345");
        client.LastSendRequest.Should().NotBeNull();
        client.LastSendRequest!.Text.Should().Be("hello world");
        client.LastSendRequest!.ParseMode.Should().BeNull();
    }

    [Fact]
    public async Task SendMessage_validates_inputs()
    {
        var tool = new TelegramMessagesSendTool(new StubTelegramNyxClient());

        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"chat_id":"12345","text":"hi"}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-abc"))
        {
            (await tool.ExecuteAsync("""{"chat_id":" ","text":"hi"}"""))
                .Should().Contain("chat_id is required");
            (await tool.ExecuteAsync("""{"chat_id":"12345","text":""}"""))
                .Should().Contain("text is required");
            (await tool.ExecuteAsync("""{"chat_id":"12345","text":"hi","parse_mode":"plaintext"}"""))
                .Should().Contain("parse_mode must be one of");
        }
    }

    [Fact]
    public async Task SendMessage_surfaces_proxy_and_telegram_errors_with_chat_id()
    {
        var nyxErrorTool = new TelegramMessagesSendTool(new StubTelegramNyxClient
        {
            SendResponse = """{"error":true,"status":503,"message":"upstream offline"}""",
        });
        using (new AgentToolRequestMetadataScope("token-abc"))
        {
            var result = await nyxErrorTool.ExecuteAsync("""{"chat_id":"99","text":"hi"}""");
            result.Should().Contain("nyx_proxy_error status=503");
            result.Should().Contain("\"chat_id\":\"99\"");
        }

        var tgErrorTool = new TelegramMessagesSendTool(new StubTelegramNyxClient
        {
            SendResponse = """{"ok":false,"error_code":403,"description":"Forbidden: bot was blocked by the user"}""",
        });
        using (new AgentToolRequestMetadataScope("token-abc"))
        {
            var result = await tgErrorTool.ExecuteAsync("""{"chat_id":"99","text":"hi"}""");
            result.Should().Contain("telegram_error_code=403");
            result.Should().Contain("Forbidden: bot was blocked by the user");
        }
    }

    [Fact]
    public async Task ChatsLookup_returns_chat_metadata()
    {
        var client = new StubTelegramNyxClient
        {
            GetChatResponse = """{"ok":true,"result":{"id":-1001234,"type":"supergroup","title":"My Group","username":"my_group"}}""",
        };
        var tool = new TelegramChatsLookupTool(client);

        using var _ = new AgentToolRequestMetadataScope("token-abc");
        var result = await tool.ExecuteAsync("""{"chat_id":"-1001234"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("chat_id").GetString().Should().Be("-1001234");
        document.RootElement.GetProperty("type").GetString().Should().Be("supergroup");
        document.RootElement.GetProperty("title").GetString().Should().Be("My Group");
        document.RootElement.GetProperty("username").GetString().Should().Be("my_group");
        client.LastGetChatRequest.Should().NotBeNull();
        client.LastGetChatRequest!.ChatId.Should().Be("-1001234");
    }

    [Fact]
    public async Task ChatsLookup_validates_inputs()
    {
        var tool = new TelegramChatsLookupTool(new StubTelegramNyxClient());

        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"chat_id":"99"}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-abc"))
        {
            (await tool.ExecuteAsync("""{"chat_id":""}"""))
                .Should().Contain("chat_id is required");
        }
    }

    [Fact]
    public async Task ToolSource_emits_no_tools_when_nyx_base_url_missing()
    {
        var source = new TelegramAgentToolSource(
            new TelegramToolOptions(),
            new NyxId.NyxIdToolOptions { BaseUrl = null },
            new StubTelegramNyxClient());

        var tools = await source.DiscoverToolsAsync();
        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolSource_emits_send_and_lookup_tools_when_configured()
    {
        var source = new TelegramAgentToolSource(
            new TelegramToolOptions(),
            new NyxId.NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new StubTelegramNyxClient());

        var tools = await source.DiscoverToolsAsync();
        tools.Select(t => t.Name).Should().BeEquivalentTo("telegram_messages_send", "telegram_chats_lookup");
    }

    [Fact]
    public async Task ToolSource_respects_disable_flags()
    {
        var source = new TelegramAgentToolSource(
            new TelegramToolOptions
            {
                EnableMessageSend = false,
                EnableChatLookup = false,
            },
            new NyxId.NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new StubTelegramNyxClient());

        var tools = await source.DiscoverToolsAsync();
        tools.Should().BeEmpty();
    }

    private sealed class StubTelegramNyxClient : ITelegramNyxClient
    {
        public string? SendResponse { get; set; }
        public string? GetChatResponse { get; set; }
        public TelegramSendMessageRequest? LastSendRequest { get; private set; }
        public TelegramGetChatRequest? LastGetChatRequest { get; private set; }

        public Task<string> SendMessageAsync(string token, TelegramSendMessageRequest request, CancellationToken ct)
        {
            LastSendRequest = request;
            return Task.FromResult(SendResponse ?? """{"ok":true,"result":{"message_id":1,"chat":{"id":1,"type":"private"},"date":0}}""");
        }

        public Task<string> GetChatAsync(string token, TelegramGetChatRequest request, CancellationToken ct)
        {
            LastGetChatRequest = request;
            return Task.FromResult(GetChatResponse ?? """{"ok":true,"result":{"id":1,"type":"private"}}""");
        }
    }

    private sealed class AgentToolRequestMetadataScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string>? _previous;

        public AgentToolRequestMetadataScope(string? accessToken = null)
        {
            _previous = AgentToolRequestContext.CurrentMetadata;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                AgentToolRequestContext.CurrentMetadata = null;
                return;
            }

            AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = accessToken,
            };
        }

        public void Dispose() => AgentToolRequestContext.CurrentMetadata = _previous;
    }
}
