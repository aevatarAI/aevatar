using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Telegram;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.ToolProviders.Telegram.Tests;

/// <summary>
/// Direct HTTP-level coverage for <see cref="TelegramNyxClient"/>. Uses a recording
/// <see cref="HttpMessageHandler"/> so every body field, slug routing, and method assertion is exercised
/// without relying on the in-process stub used by the higher-level tool tests.
/// </summary>
public sealed class TelegramNyxClientTests
{
    [Fact]
    public async Task SendMessage_posts_chat_id_text_only_body_to_proxy_slug()
    {
        var (client, handler) = CreateClient();

        await client.SendMessageAsync(
            "user-token",
            new TelegramSendMessageRequest(ChatId: "12345", Text: "hello"),
            CancellationToken.None);

        var request = handler.LastRequest!;
        request.Method.Should().Be(HttpMethod.Post);
        request.Path.Should().Be("/api/v1/proxy/s/api-telegram-bot/sendMessage");
        request.AuthorizationHeader.Should().Be("Bearer user-token");

        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("chat_id").GetString().Should().Be("12345");
        body.RootElement.GetProperty("text").GetString().Should().Be("hello");
        body.RootElement.TryGetProperty("parse_mode", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("disable_notification", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("reply_to_message_id", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("reply_markup", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SendMessage_includes_optional_fields_only_when_set()
    {
        var (client, handler) = CreateClient();

        await client.SendMessageAsync(
            "user-token",
            new TelegramSendMessageRequest(
                ChatId: "12345",
                Text: "hello",
                ParseMode: "MarkdownV2",
                DisableNotification: true,
                ReplyToMessageId: 42),
            CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("parse_mode").GetString().Should().Be("MarkdownV2");
        body.RootElement.GetProperty("disable_notification").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("reply_to_message_id").GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task SendMessage_omits_disable_notification_when_explicitly_false()
    {
        // The client only adds disable_notification when caller explicitly opted in (true);
        // false / null both keep the body lean so Telegram applies its default behavior.
        var (client, handler) = CreateClient();

        await client.SendMessageAsync(
            "user-token",
            new TelegramSendMessageRequest(
                ChatId: "12345",
                Text: "hello",
                DisableNotification: false),
            CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.TryGetProperty("disable_notification", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SendMessage_parses_reply_markup_json_into_object_field()
    {
        // Without the JsonNode.Parse path, reply_markup would arrive as a JSON string at Telegram
        // and be rejected; this asserts it lands as a structured object.
        var (client, handler) = CreateClient();

        await client.SendMessageAsync(
            "user-token",
            new TelegramSendMessageRequest(
                ChatId: "12345",
                Text: "hello",
                ReplyMarkupJson: """{"inline_keyboard":[[{"text":"yes","callback_data":"y"}]]}"""),
            CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastBody!);
        var markup = body.RootElement.GetProperty("reply_markup");
        markup.ValueKind.Should().Be(JsonValueKind.Object);
        var firstButton = markup.GetProperty("inline_keyboard")[0][0];
        firstButton.GetProperty("text").GetString().Should().Be("yes");
        firstButton.GetProperty("callback_data").GetString().Should().Be("y");
    }

    [Fact]
    public async Task SendMessage_returns_response_body_verbatim()
    {
        var (client, handler) = CreateClient();
        handler.NextResponseBody = """{"ok":true,"result":{"message_id":7}}""";

        var response = await client.SendMessageAsync(
            "user-token",
            new TelegramSendMessageRequest(ChatId: "1", Text: "hi"),
            CancellationToken.None);

        response.Should().Contain("\"message_id\":7");
    }

    [Fact]
    public async Task GetChat_posts_chat_id_body_to_get_chat_slug()
    {
        var (client, handler) = CreateClient();

        await client.GetChatAsync(
            "user-token",
            new TelegramGetChatRequest(ChatId: "-1001234"),
            CancellationToken.None);

        var request = handler.LastRequest!;
        request.Method.Should().Be(HttpMethod.Post);
        request.Path.Should().Be("/api/v1/proxy/s/api-telegram-bot/getChat");
        request.AuthorizationHeader.Should().Be("Bearer user-token");

        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("chat_id").GetString().Should().Be("-1001234");
    }

    [Fact]
    public async Task SendMessage_uses_overridden_provider_slug()
    {
        // Custom configuration must be honored — Mainnet host wires this through
        // Aevatar:Telegram:NyxProviderSlug (see MainnetHostBuilderExtensions).
        var nyxOptions = new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" };
        var handler = new RecordingHandler();
        var client = new TelegramNyxClient(
            new TelegramToolOptions { ProviderSlug = "api-telegram-staging" },
            new NyxIdApiClient(nyxOptions, new HttpClient(handler)));

        await client.SendMessageAsync(
            "user-token",
            new TelegramSendMessageRequest(ChatId: "1", Text: "hi"),
            CancellationToken.None);

        handler.LastRequest!.Path.Should().Be("/api/v1/proxy/s/api-telegram-staging/sendMessage");
    }

    private static (TelegramNyxClient Client, RecordingHandler Handler) CreateClient()
    {
        var nyxOptions = new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" };
        var handler = new RecordingHandler();
        var client = new TelegramNyxClient(
            new TelegramToolOptions(),
            new NyxIdApiClient(nyxOptions, new HttpClient(handler)));
        return (client, handler);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public RecordedRequest? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public string NextResponseBody { get; set; } = """{"ok":true,"result":{}}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            LastRequest = new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString() ?? string.Empty);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(NextResponseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string AuthorizationHeader);
}
