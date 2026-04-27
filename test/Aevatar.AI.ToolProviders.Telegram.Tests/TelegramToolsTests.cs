using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Telegram;
using Aevatar.AI.ToolProviders.Telegram.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.AI.ToolProviders.Telegram.Tests;

public class TelegramToolsTests
{
    [Fact]
    public void Tool_metadata_is_stable()
    {
        // Pin the metadata the agent framework reads from each tool — Name flows into
        // tool registration, Description into the tool spec presented to the LLM,
        // ApprovalMode into the auto-execute decision. Drift in any of these silently
        // changes agent behavior, so they are worth pinning.
        var send = new TelegramMessagesSendTool(new StubTelegramNyxClient());
        send.Name.Should().Be("telegram_messages_send");
        send.Description.Should().Contain("Telegram").And.Contain("Nyx");
        send.ApprovalMode.Should().Be(ToolApprovalMode.Auto);

        var lookup = new TelegramChatsLookupTool(new StubTelegramNyxClient());
        lookup.Name.Should().Be("telegram_chats_lookup");
        lookup.Description.Should().Contain("Telegram").And.Contain("chat");
        lookup.ApprovalMode.Should().Be(ToolApprovalMode.Auto);
    }

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
    public async Task SendMessage_propagates_optional_fields_to_client()
    {
        var client = new StubTelegramNyxClient();
        var tool = new TelegramMessagesSendTool(client);

        using var _ = new AgentToolRequestMetadataScope("token-abc");
        await tool.ExecuteAsync(
            """{"chat_id":"12345","text":"hi","parse_mode":"MarkdownV2","disable_notification":true,"reply_to_message_id":42}""");

        client.LastSendRequest.Should().NotBeNull();
        client.LastSendRequest!.ParseMode.Should().Be("MarkdownV2");
        client.LastSendRequest.DisableNotification.Should().BeTrue();
        client.LastSendRequest.ReplyToMessageId.Should().Be(42);
    }

    [Theory]
    [InlineData("Markdown")]
    [InlineData("MarkdownV2")]
    [InlineData("HTML")]
    public async Task SendMessage_accepts_each_supported_parse_mode(string parseMode)
    {
        var client = new StubTelegramNyxClient();
        var tool = new TelegramMessagesSendTool(client);

        using var _ = new AgentToolRequestMetadataScope("token-abc");
        var json = $$"""{"chat_id":"12345","text":"hi","parse_mode":"{{parseMode}}"}""";
        var result = await tool.ExecuteAsync(json);

        result.Should().Contain("\"success\":true");
        client.LastSendRequest!.ParseMode.Should().Be(parseMode);
    }

    [Fact]
    public async Task SendMessage_surfaces_empty_response_as_error()
    {
        var tool = new TelegramMessagesSendTool(new StubTelegramNyxClient { SendResponse = string.Empty });

        using var _ = new AgentToolRequestMetadataScope("token-abc");
        var result = await tool.ExecuteAsync("""{"chat_id":"99","text":"hi"}""");
        result.Should().Contain("empty_telegram_response");
    }

    [Fact]
    public async Task SendMessage_surfaces_invalid_json_response_as_error()
    {
        var tool = new TelegramMessagesSendTool(new StubTelegramNyxClient { SendResponse = "not-json-at-all" });

        using var _ = new AgentToolRequestMetadataScope("token-abc");
        var result = await tool.ExecuteAsync("""{"chat_id":"99","text":"hi"}""");
        result.Should().Contain("invalid_telegram_response_json");
    }

    [Fact]
    public async Task SendMessage_handles_unknown_telegram_error_code()
    {
        // ok:false without error_code/description still produces a structured error string.
        var tool = new TelegramMessagesSendTool(new StubTelegramNyxClient
        {
            SendResponse = """{"ok":false}""",
        });

        using var _ = new AgentToolRequestMetadataScope("token-abc");
        var result = await tool.ExecuteAsync("""{"chat_id":"99","text":"hi"}""");
        result.Should().Contain("telegram_error_code=unknown");
    }

    [Fact]
    public async Task SendMessage_handles_unknown_nyx_status()
    {
        // error:true without status/message/body still produces a structured error string.
        var tool = new TelegramMessagesSendTool(new StubTelegramNyxClient
        {
            SendResponse = """{"error":true}""",
        });

        using var _ = new AgentToolRequestMetadataScope("token-abc");
        var result = await tool.ExecuteAsync("""{"chat_id":"99","text":"hi"}""");
        result.Should().Contain("nyx_proxy_error status=unknown");
    }

    [Fact]
    public async Task SendMessage_falls_back_to_request_chat_id_when_response_omits_result()
    {
        // Telegram could in theory return ok:true with no result block; the tool should still
        // surface chat_id from the original request rather than emitting empty.
        var tool = new TelegramMessagesSendTool(new StubTelegramNyxClient
        {
            SendResponse = """{"ok":true}""",
        });

        using var _ = new AgentToolRequestMetadataScope("token-abc");
        var result = await tool.ExecuteAsync("""{"chat_id":"99","text":"hi"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("chat_id").GetString().Should().Be("99");
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
    public async Task TelegramNyxClient_throws_argument_exception_for_malformed_reply_markup_json()
    {
        var nyxOptions = new NyxId.NyxIdToolOptions { BaseUrl = "https://nyx.example.com" };
        var client = new TelegramNyxClient(
            new TelegramToolOptions(),
            new NyxId.NyxIdApiClient(nyxOptions, new HttpClient(new ThrowingHandler())));

        var act = async () => await client.SendMessageAsync(
            "token-abc",
            new TelegramSendMessageRequest(ChatId: "1", Text: "hi", ReplyMarkupJson: "{not-json"),
            CancellationToken.None);

        var assertion = await act.Should().ThrowAsync<ArgumentException>();
        assertion.Which.Message.Should().Contain("ReplyMarkupJson");
        assertion.Which.ParamName.Should().Be("request");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("nyx client should not be reached when reply_markup_json is invalid");
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
    public async Task ChatsLookup_handles_chat_id_as_number_in_response()
    {
        // Telegram returns chat.id as a JSON number; the parser must coerce it to string for
        // the tool output.
        var tool = new TelegramChatsLookupTool(new StubTelegramNyxClient
        {
            GetChatResponse = """{"ok":true,"result":{"id":42,"type":"private"}}""",
        });

        using var _ = new AgentToolRequestMetadataScope("token-abc");
        var result = await tool.ExecuteAsync("""{"chat_id":"42"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("chat_id").GetString().Should().Be("42");
        document.RootElement.GetProperty("type").GetString().Should().Be("private");
    }

    [Fact]
    public async Task ChatsLookup_falls_back_to_request_chat_id_when_response_omits_result()
    {
        var tool = new TelegramChatsLookupTool(new StubTelegramNyxClient
        {
            GetChatResponse = """{"ok":true}""",
        });

        using var _ = new AgentToolRequestMetadataScope("token-abc");
        var result = await tool.ExecuteAsync("""{"chat_id":"99"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("chat_id").GetString().Should().Be("99");
    }

    [Fact]
    public async Task ChatsLookup_surfaces_proxy_and_telegram_errors_with_chat_id()
    {
        var nyxErrorTool = new TelegramChatsLookupTool(new StubTelegramNyxClient
        {
            GetChatResponse = """{"error":true,"status":502,"body":"upstream timed out"}""",
        });
        using (new AgentToolRequestMetadataScope("token-abc"))
        {
            var result = await nyxErrorTool.ExecuteAsync("""{"chat_id":"99"}""");
            result.Should().Contain("nyx_proxy_error status=502");
            result.Should().Contain("upstream timed out");
            result.Should().Contain("\"chat_id\":\"99\"");
        }

        var tgErrorTool = new TelegramChatsLookupTool(new StubTelegramNyxClient
        {
            GetChatResponse = """{"ok":false,"error_code":400,"description":"chat not found"}""",
        });
        using (new AgentToolRequestMetadataScope("token-abc"))
        {
            var result = await tgErrorTool.ExecuteAsync("""{"chat_id":"99"}""");
            result.Should().Contain("telegram_error_code=400");
            result.Should().Contain("chat not found");
        }
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

    [Fact]
    public async Task ToolSource_emits_only_send_when_chat_lookup_disabled()
    {
        var source = new TelegramAgentToolSource(
            new TelegramToolOptions { EnableChatLookup = false },
            new NyxId.NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new StubTelegramNyxClient());

        var tools = await source.DiscoverToolsAsync();
        tools.Select(t => t.Name).Should().BeEquivalentTo("telegram_messages_send");
    }

    [Fact]
    public async Task ToolSource_emits_only_lookup_when_send_disabled()
    {
        var source = new TelegramAgentToolSource(
            new TelegramToolOptions { EnableMessageSend = false },
            new NyxId.NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new StubTelegramNyxClient());

        var tools = await source.DiscoverToolsAsync();
        tools.Select(t => t.Name).Should().BeEquivalentTo("telegram_chats_lookup");
    }

    [Fact]
    public void AddTelegramTools_registers_options_client_and_tool_source()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" });
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient()));

        services.AddTelegramTools(o => o.ProviderSlug = "api-telegram-staging");

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<TelegramToolOptions>().ProviderSlug.Should().Be("api-telegram-staging");
        provider.GetRequiredService<ITelegramNyxClient>().Should().BeOfType<TelegramNyxClient>();
        provider.GetServices<IAgentToolSource>().OfType<TelegramAgentToolSource>().Should().ContainSingle();
    }

    [Fact]
    public void AddTelegramTools_uses_default_options_when_configure_omitted()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new NyxIdToolOptions());
        services.AddSingleton(new NyxIdApiClient(new NyxIdToolOptions(), new HttpClient()));

        services.AddTelegramTools();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<TelegramToolOptions>().ProviderSlug.Should().Be("api-telegram-bot");
    }

    [Fact]
    public async Task ToolSource_emits_no_tools_when_provider_slug_blank()
    {
        var source = new TelegramAgentToolSource(
            new TelegramToolOptions { ProviderSlug = string.Empty },
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
