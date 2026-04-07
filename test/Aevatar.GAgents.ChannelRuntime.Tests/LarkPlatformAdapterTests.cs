using System.Text;
using System.Text.Json;
using Aevatar.GAgents.ChannelRuntime.Adapters;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class LarkPlatformAdapterTests
{
    private readonly LarkPlatformAdapter _adapter = new(NullLogger<LarkPlatformAdapter>.Instance);

    private static ChannelBotRegistrationEntry MakeRegistration() => new()
    {
        Id = "test-reg-1",
        Platform = "lark",
        NyxProviderSlug = "api-lark-bot",
        NyxUserToken = "test-token",
        VerificationToken = "verify-token",
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
    public void Platform_returns_lark()
    {
        _adapter.Platform.Should().Be("lark");
    }

    [Fact]
    public async Task TryHandleVerification_returns_challenge_for_url_verification()
    {
        var payload = new
        {
            type = "url_verification",
            challenge = "test-challenge-123",
            token = "verify-token",
        };

        var http = CreateHttpContext(payload);
        var result = await _adapter.TryHandleVerificationAsync(http, MakeRegistration());

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task TryHandleVerification_rejects_mismatched_token()
    {
        var payload = new
        {
            type = "url_verification",
            challenge = "test-challenge-123",
            token = "wrong-token",
        };

        var http = CreateHttpContext(payload);
        var result = await _adapter.TryHandleVerificationAsync(http, MakeRegistration());

        // Should return an IResult (Unauthorized), not null
        result.Should().NotBeNull();
        // The result should be an UnauthorizedHttpResult (401)
        result.Should().BeOfType(typeof(Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult));
    }

    [Fact]
    public async Task TryHandleVerification_accepts_matching_token()
    {
        var payload = new
        {
            type = "url_verification",
            challenge = "test-challenge-ok",
            token = "verify-token",
        };

        var http = CreateHttpContext(payload);
        var result = await _adapter.TryHandleVerificationAsync(http, MakeRegistration());

        // Should return a JsonHttpResult (the challenge echo), not Unauthorized
        result.Should().NotBeNull();
        result.Should().NotBeOfType(typeof(Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult));
    }

    [Fact]
    public async Task TryHandleVerification_allows_when_no_verification_token_configured()
    {
        var payload = new
        {
            type = "url_verification",
            challenge = "test-challenge-no-verify",
            token = "any-token",
        };

        // Registration with empty verification token — should skip check
        var reg = new ChannelBotRegistrationEntry
        {
            Id = "test-reg-no-verify",
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            NyxUserToken = "test-token",
            VerificationToken = "",
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        var http = CreateHttpContext(payload);
        var result = await _adapter.TryHandleVerificationAsync(http, reg);

        result.Should().NotBeNull();
        result.Should().NotBeOfType(typeof(Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult));
    }

    [Fact]
    public async Task ParseInbound_rejects_mismatched_event_token()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "im.message.receive_v1", token = "wrong-token" },
            @event = new
            {
                sender = new { sender_id = new { open_id = "ou_abc" }, sender_type = "user" },
                message = new
                {
                    chat_id = "oc_chat1",
                    message_type = "text",
                    content = JsonSerializer.Serialize(new { text = "hello" }),
                },
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task TryHandleVerification_returns_null_for_normal_event()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "im.message.receive_v1" },
            @event = new { message = new { chat_id = "oc_123", content = "{\"text\":\"hi\"}", message_type = "text" } },
        };

        var http = CreateHttpContext(payload);
        var result = await _adapter.TryHandleVerificationAsync(http, MakeRegistration());

        result.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_extracts_text_message()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "im.message.receive_v1", token = "verify-token" },
            @event = new
            {
                sender = new
                {
                    sender_id = new { open_id = "ou_abc123" },
                    sender_type = "user",
                },
                message = new
                {
                    chat_id = "oc_chat456",
                    message_id = "om_msg789",
                    message_type = "text",
                    chat_type = "p2p",
                    content = JsonSerializer.Serialize(new { text = "Hello from Lark!" }),
                },
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().NotBeNull();
        inbound!.Platform.Should().Be("lark");
        inbound.ConversationId.Should().Be("oc_chat456");
        inbound.SenderId.Should().Be("ou_abc123");
        inbound.Text.Should().Be("Hello from Lark!");
        inbound.MessageId.Should().Be("om_msg789");
        inbound.ChatType.Should().Be("p2p");
    }

    [Fact]
    public async Task ParseInbound_returns_null_for_non_message_event()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "im.chat.member.bot.added_v1" },
            @event = new { },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_returns_null_for_empty_text()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "im.message.receive_v1" },
            @event = new
            {
                sender = new { sender_id = new { open_id = "ou_abc" } },
                message = new
                {
                    chat_id = "oc_chat1",
                    message_type = "image",
                    content = "{}",
                },
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_ignores_bot_sender()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "im.message.receive_v1" },
            @event = new
            {
                sender = new
                {
                    sender_id = new { open_id = "ou_bot" },
                    sender_type = "bot",
                },
                message = new
                {
                    chat_id = "oc_chat1",
                    message_type = "text",
                    content = JsonSerializer.Serialize(new { text = "bot message" }),
                },
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task ParseInbound_returns_null_when_missing_header()
    {
        var payload = new { schema = "2.0" };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().BeNull();
    }
}
