using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Telegram;
using Shouldly;

namespace Aevatar.GAgents.Platform.Telegram.Tests;

public sealed class TelegramChannelNativeMessageSenderTests
{
    [Fact]
    public async Task UpdateAsync_ShouldUseEditMessageText()
    {
        var handler = new RecordingHandler("""{"ok":true,"result":{"message_id":42}}""");
        var sender = new TelegramChannelNativeMessageSender(CreateNyxClient(handler));
        var target = new ChannelNativeDeliveryTarget(
            AgentId: "agent-1",
            Platform: "telegram",
            ConversationId: "chat-1",
            NyxProviderSlug: "api-telegram-bot",
            NyxApiKey: "nyx-api-key-1");

        var result = await sender.UpdateAsync(
            target,
            "42",
            new ChannelNativeMessage("hello updated", CardPayload: null, MessageType: "text", ComposeCapability.Exact),
            isFinal: false,
            cancellationToken: CancellationToken.None);

        result.SentActivityId.ShouldBe("42");
        result.PlatformMessageId.ShouldBe("42");
        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath
            .ShouldBe("/api/v1/proxy/s/api-telegram-bot/editMessageText");
        handler.LastBody.ShouldNotBeNull();
        var lastBody = handler.LastBody!;
        lastBody.ShouldContain("\"chat_id\":\"chat-1\"");
        lastBody.ShouldContain("\"message_id\":42");
        lastBody.ShouldContain("\"text\":\"hello updated\"");
    }

    private static NyxIdApiClient CreateNyxClient(HttpMessageHandler handler) =>
        new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
