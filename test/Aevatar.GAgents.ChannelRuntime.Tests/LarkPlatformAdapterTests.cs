using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using Aevatar.GAgents.ChannelRuntime.Adapters;
using Aevatar.AI.ToolProviders.NyxId;
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
    public async Task ParseInbound_extracts_card_action_with_resume_fields()
    {
        var payload = new
        {
            schema = "2.0",
            header = new { event_type = "card.action.trigger", token = "verify-token", event_id = "evt_card_123" },
            @event = new
            {
                @operator = new
                {
                    open_id = "ou_operator_1",
                },
                context = new
                {
                    open_chat_id = "oc_chat_card_1",
                    open_message_id = "om_card_msg_1",
                },
                action = new
                {
                    value = new
                    {
                        actor_id = "run-actor-1",
                        run_id = "run-1",
                        step_id = "approval-1",
                        approved = true,
                    },
                    form_value = new
                    {
                        user_input = "Looks good",
                    },
                },
            },
        };

        var http = CreateHttpContext(payload);
        var inbound = await _adapter.ParseInboundAsync(http, MakeRegistration());

        inbound.Should().NotBeNull();
        inbound!.ChatType.Should().Be("card_action");
        inbound.ConversationId.Should().Be("oc_chat_card_1");
        inbound.SenderId.Should().Be("ou_operator_1");
        inbound.MessageId.Should().Be("evt_card_123");
        inbound.Extra.Should().Contain(new KeyValuePair<string, string>("actor_id", "run-actor-1"));
        inbound.Extra.Should().Contain(new KeyValuePair<string, string>("run_id", "run-1"));
        inbound.Extra.Should().Contain(new KeyValuePair<string, string>("step_id", "approval-1"));
        inbound.Extra.Should().Contain(new KeyValuePair<string, string>("approved", "True"));
        inbound.Extra.Should().Contain(new KeyValuePair<string, string>("user_input", "Looks good"));
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

    [Fact]
    public async Task SendReplyAsync_returns_success_detail_when_lark_accepts_message()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.OK, """
            {"code":0,"msg":"success","data":{"message_id":"om_success_123"}}
            """, out _);
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_123",
            SenderId = "ou_sender_1",
            SenderName = "sender-1",
            Text = "hello",
        };

        var result = await _adapter.SendReplyAsync("reply", inbound, MakeRegistration(), nyxClient, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Detail.Should().Be("message_id=om_success_123");
    }

    [Fact]
    public async Task SendReplyAsync_returns_failed_result_when_lark_rejects_message()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.OK, """
            {"code":230001,"msg":"invalid receive id"}
            """, out _);
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_123",
            SenderId = "ou_sender_1",
            SenderName = "sender-1",
            Text = "hello",
        };

        var result = await _adapter.SendReplyAsync("reply", inbound, MakeRegistration(), nyxClient, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Be("lark_code=230001 msg=invalid receive id");
    }

    [Fact]
    public async Task SendReplyAsync_uses_interactive_message_type_for_card_payload()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.OK, """
            {"code":0,"msg":"success","data":{"message_id":"om_card_123"}}
            """, out var handler);
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            httpClient);
        var inbound = new InboundMessage
        {
            Platform = "lark",
            ConversationId = "oc_chat_123",
            SenderId = "ou_sender_1",
            SenderName = "sender-1",
            Text = "hello",
        };
        const string replyText = """
            {"header":{"title":{"tag":"plain_text","content":"Approval"}},"elements":[]}
            """;

        var result = await _adapter.SendReplyAsync(replyText, inbound, MakeRegistration(), nyxClient, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        handler.LastRequestBody.Should().Contain("\"msg_type\":\"interactive\"");
        handler.LastRequestBody.Should().Contain("\\u0022header\\u0022");
    }

    [Fact]
    public void IsInteractiveCardPayload_detects_feishu_card_json()
    {
        LarkPlatformAdapter.IsInteractiveCardPayload("""{"elements":[]}""").Should().BeTrue();
        LarkPlatformAdapter.IsInteractiveCardPayload("hello").Should().BeFalse();
    }

    private static HttpClient CreateHttpClient(HttpStatusCode statusCode, string body, out StaticResponseHandler handler)
    {
        handler = new StaticResponseHandler(statusCode, body);
        return new HttpClient(handler)
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
