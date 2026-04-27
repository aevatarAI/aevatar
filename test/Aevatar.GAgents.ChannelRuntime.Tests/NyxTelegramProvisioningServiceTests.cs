using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class NyxTelegramProvisioningServiceTests
{
    [Fact]
    public async Task ProvisionAsync_creates_nyx_resources_and_dispatches_local_mirror()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", """{"id":"key-tg-1","full_key":"full-key"}""");
        handler.Enqueue("/api/v1/channel-bots", """{"id":"bot-tg-1","status":"pending_webhook"}""");
        handler.Enqueue("/api/v1/channel-conversations", """{"id":"route-tg-1","default_agent":true}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));

        EventEnvelope? capturedEnvelope = null;
        var actor = Substitute.For<IActor>();
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(actor));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var service = new NyxTelegramProvisioningService(
            nyxClient,
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            actorRuntime,
            (IActorDispatchPort)actorRuntime,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxTelegramProvisioningService>>());

        var result = await service.ProvisionAsync(
            new NyxTelegramProvisioningRequest(
                AccessToken: "user-token",
                BotToken: "1234567890:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef-hi",
                WebhookBaseUrl: "https://aevatar.example.com",
                ScopeId: "scope-1",
                Label: "Ops Bot",
                NyxProviderSlug: "api-telegram-bot"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Status.Should().Be("accepted");
        result.RegistrationId.Should().NotBeNullOrWhiteSpace();
        result.NyxAgentApiKeyId.Should().Be("key-tg-1");
        result.NyxChannelBotId.Should().Be("bot-tg-1");
        result.NyxConversationRouteId.Should().Be("route-tg-1");
        result.RelayCallbackUrl.Should().Be("https://aevatar.example.com/api/webhooks/nyxid-relay");
        result.WebhookUrl.Should().Be("https://nyx.example.com/api/v1/webhooks/channel/telegram/bot-tg-1");

        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Is(ChannelBotRegisterCommand.Descriptor).Should().BeTrue();
        var command = capturedEnvelope.Payload.Unpack<ChannelBotRegisterCommand>();
        command.Platform.Should().Be("telegram");
        command.NyxProviderSlug.Should().Be("api-telegram-bot");
        command.NyxAgentApiKeyId.Should().Be("key-tg-1");
        command.NyxChannelBotId.Should().Be("bot-tg-1");
        command.NyxConversationRouteId.Should().Be("route-tg-1");
        command.WebhookUrl.Should().Be("https://nyx.example.com/api/v1/webhooks/channel/telegram/bot-tg-1");

        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].Body.Should().Contain("\"callback_url\":\"https://aevatar.example.com/api/webhooks/nyxid-relay\"");
        handler.Requests[0].Body.Should().Contain("\"platform\":\"generic\"");
        handler.Requests[1].Body.Should().Contain("\"platform\":\"telegram\"");
        handler.Requests[1].Body.Should().Contain("\"bot_token\":\"1234567890:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef-hi\"");
        handler.Requests[1].Body.Should().NotContain("__unused_for_lark__");
        handler.Requests[2].Body.Should().Contain("\"default_agent\":true");
    }

    [Theory]
    [InlineData("", "bot-token", "https://aevatar.example.com", "scope-1", "missing_access_token")]
    [InlineData("user-token", "", "https://aevatar.example.com", "scope-1", "missing_bot_token")]
    [InlineData("user-token", "bot-token", "", "scope-1", "missing_webhook_base_url")]
    [InlineData("user-token", "bot-token", "https://aevatar.example.com", "", "missing_scope_id")]
    public async Task ProvisionAsync_rejects_invalid_requests_before_calling_nyx(
        string accessToken,
        string botToken,
        string webhookBaseUrl,
        string scopeId,
        string expectedError)
    {
        var handler = new RecordingHandler();
        var service = CreateService(handler);

        var result = await service.ProvisionAsync(
            new NyxTelegramProvisioningRequest(
                AccessToken: accessToken,
                BotToken: botToken,
                WebhookBaseUrl: webhookBaseUrl,
                ScopeId: scopeId,
                Label: "Ops Bot",
                NyxProviderSlug: "api-telegram-bot"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(expectedError);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task INyxChannelBotProvisioningService_reads_bot_token_from_credentials_map()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", """{"id":"key-tg-2"}""");
        handler.Enqueue("/api/v1/channel-bots", """{"id":"bot-tg-2"}""");
        handler.Enqueue("/api/v1/channel-conversations", """{"id":"route-tg-2"}""");
        var service = CreateService(handler);

        var generic = (INyxChannelBotProvisioningService)service;
        var result = await generic.ProvisionAsync(
            new NyxChannelBotProvisioningRequest(
                Platform: "telegram",
                AccessToken: "user-token",
                WebhookBaseUrl: "https://aevatar.example.com",
                ScopeId: "scope-1",
                Label: "Ops Bot",
                NyxProviderSlug: "api-telegram-bot",
                Credentials: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bot_token"] = "tok-from-map",
                }),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Platform.Should().Be("telegram");
        handler.Requests[1].Body.Should().Contain("\"bot_token\":\"tok-from-map\"");
    }

    [Fact]
    public async Task INyxChannelBotProvisioningService_returns_missing_bot_token_when_credentials_absent()
    {
        var handler = new RecordingHandler();
        var service = CreateService(handler);
        var generic = (INyxChannelBotProvisioningService)service;

        var result = await generic.ProvisionAsync(
            new NyxChannelBotProvisioningRequest(
                Platform: "telegram",
                AccessToken: "user-token",
                WebhookBaseUrl: "https://aevatar.example.com",
                ScopeId: "scope-1",
                Label: "Ops Bot",
                NyxProviderSlug: "api-telegram-bot"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("missing_bot_token");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task INyxChannelBotProvisioningService_rejects_non_telegram_platform()
    {
        var handler = new RecordingHandler();
        var service = CreateService(handler);
        var generic = (INyxChannelBotProvisioningService)service;

        var result = await generic.ProvisionAsync(
            new NyxChannelBotProvisioningRequest(
                Platform: "lark",
                AccessToken: "user-token",
                WebhookBaseUrl: "https://aevatar.example.com",
                ScopeId: "scope-1",
                Label: "Ops Bot",
                NyxProviderSlug: "api-telegram-bot",
                Credentials: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bot_token"] = "tok",
                }),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("unsupported_platform");
        handler.Requests.Should().BeEmpty();
    }

    private static NyxTelegramProvisioningService CreateService(RecordingHandler handler)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        return new NyxTelegramProvisioningService(
            nyxClient,
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            actorRuntime,
            (IActorDispatchPort)actorRuntime,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxTelegramProvisioningService>>());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpMethod? Method, string Path, string Body)> _responses = new();

        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

        public void Enqueue(string path, string body) => _responses.Enqueue((null, path, body));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
                throw new InvalidOperationException("No more queued responses.");

            var (expectedMethod, expectedPath, responseBody) = _responses.Dequeue();
            request.RequestUri.Should().NotBeNull();
            request.RequestUri!.AbsolutePath.Should().Be(expectedPath);
            if (expectedMethod is not null)
                request.Method.Should().Be(expectedMethod);

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, expectedPath, body));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
