using System.Text;
using System.Text.Json;
using Aevatar.GAgents.ChannelRuntime.Adapters;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class TelegramPlatformAdapterTests
{
    private readonly TelegramPlatformAdapter _adapter = new(NullLogger<TelegramPlatformAdapter>.Instance);

    private static ChannelBotRegistrationEntry MakeRegistration() => new()
    {
        Id = "test-tg-reg-1",
        Platform = "telegram",
        NyxProviderSlug = "api-telegram-bot",
        ScopeId = "test-scope",
        CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };

    private static HttpContext CreateHttpContext(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Request.ContentType = "application/json";
        context.Request.EnableBuffering();
        return context;
    }

    [Fact]
    public void Platform_returns_telegram()
    {
        _adapter.Platform.Should().Be("telegram");
    }

    [Fact]
    public async Task TryHandleVerification_always_returns_null()
    {
        var payload = new { update_id = 123 };
        var http = CreateHttpContext(payload);
        var result = await _adapter.TryHandleVerificationAsync(http, MakeRegistration());
        result.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_extracts_text_message()
    {
        var payload = new
        {
            update_id = 123456789,
            message = new
            {
                message_id = 456,
                date = 1234567890,
                chat = new { id = -987654321L, type = "group" },
                from = new { id = 12345, is_bot = false, first_name = "John", username = "john_doe" },
                text = "Hello from Telegram!",
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().NotBeNull();
        inbound!.Platform.Should().Be("telegram");
        inbound.ConversationId.Should().Be("-987654321");
        inbound.SenderId.Should().Be("12345");
        inbound.SenderName.Should().Be("john_doe");
        inbound.Text.Should().Be("Hello from Telegram!");
        inbound.MessageId.Should().Be("456");
        inbound.ChatType.Should().Be("group");
    }

    [Fact]
    public async Task ParseInbound_extracts_private_message()
    {
        var payload = new
        {
            update_id = 100,
            message = new
            {
                message_id = 1,
                date = 1234567890,
                chat = new { id = 42L, type = "private" },
                from = new { id = 42, is_bot = false, first_name = "Alice" },
                text = "Private hello",
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().NotBeNull();
        inbound!.ConversationId.Should().Be("42");
        inbound.SenderName.Should().Be("Alice"); // falls back to first_name when no username
        inbound.ChatType.Should().Be("private");
    }

    [Fact]
    public async Task ParseInbound_ignores_bot_messages()
    {
        var payload = new
        {
            update_id = 100,
            message = new
            {
                message_id = 1,
                date = 1234567890,
                chat = new { id = 42L, type = "private" },
                from = new { id = 99, is_bot = true, first_name = "OtherBot" },
                text = "I am a bot",
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_returns_null_for_non_text_message()
    {
        var payload = new
        {
            update_id = 100,
            message = new
            {
                message_id = 1,
                date = 1234567890,
                chat = new { id = 42L, type = "private" },
                from = new { id = 42, is_bot = false, first_name = "Alice" },
                // No "text" field — could be photo, sticker, etc.
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_returns_null_for_edited_message()
    {
        // Telegram sends edited_message instead of message for edits
        var payload = new
        {
            update_id = 100,
            edited_message = new
            {
                message_id = 1,
                date = 1234567890,
                chat = new { id = 42L, type = "private" },
                from = new { id = 42, is_bot = false, first_name = "Alice" },
                text = "Edited text",
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_returns_null_for_callback_query()
    {
        // Inline keyboard callback — no "message" field at top level
        var payload = new
        {
            update_id = 100,
            callback_query = new
            {
                id = "abc123",
                from = new { id = 42, is_bot = false },
                data = "button_click",
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task SendReply_returns_retired_contract_failure()
    {
        var result = await _adapter.SendReplyAsync(
            "hello",
            new InboundMessage
            {
                Platform = "telegram",
                ConversationId = "42",
                SenderId = "user-1",
                SenderName = "user-1",
                Text = "hello",
            },
            MakeRegistration(),
            new Aevatar.AI.ToolProviders.NyxId.NyxIdApiClient(
                new Aevatar.AI.ToolProviders.NyxId.NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().Be(PlatformReplyFailureKind.Permanent);
        result.Detail.Should().Contain("telegram_direct_callback_retired");
    }
}
