using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class NyxLarkProvisioningServiceTests
{
    [Fact]
    public async Task ProvisionAsync_Creates_Nyx_Resources_And_Dispatches_Local_Mirror_Without_Credentials()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", """{"id":"key-123","full_key":"full-key"}""");
        handler.Enqueue("/api/v1/channel-bots", """{"id":"bot-456","status":"pending_webhook"}""");
        handler.Enqueue("/api/v1/channel-conversations", """{"id":"route-789","default_agent":true}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));

        var actor = Substitute.For<IActor>();
        actor.Id.Returns(ChannelBotRegistrationGAgent.WellKnownId);
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(actor));

        var service = new NyxLarkProvisioningService(
            nyxClient,
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            actorRuntime,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(
            new NyxLarkProvisioningRequest(
                AccessToken: "user-token",
                AppId: "cli_a1b2c3",
                AppSecret: "secret-xyz",
                WebhookBaseUrl: "https://aevatar.example.com",
                ScopeId: "scope-1",
                Label: "Ops Bot",
                NyxProviderSlug: "api-lark-bot"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Status.Should().Be("accepted");
        result.RegistrationId.Should().NotBeNullOrWhiteSpace();
        result.NyxAgentApiKeyId.Should().Be("key-123");
        result.NyxChannelBotId.Should().Be("bot-456");
        result.NyxConversationRouteId.Should().Be("route-789");
        result.RelayCallbackUrl.Should().Be("https://aevatar.example.com/api/webhooks/nyxid-relay");
        result.WebhookUrl.Should().Be("https://nyx.example.com/api/v1/webhooks/channel/lark/bot-456");

        await actor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(envelope =>
                envelope.Payload != null &&
                envelope.Payload.Is(ChannelBotRegisterCommand.Descriptor) &&
                MatchesLocalMirror(envelope.Payload.Unpack<ChannelBotRegisterCommand>(), result.RegistrationId!)),
            Arg.Any<CancellationToken>());

        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].Body.Should().Contain("\"callback_url\":\"https://aevatar.example.com/api/webhooks/nyxid-relay\"");
        handler.Requests[1].Body.Should().Contain("\"bot_token\":\"__unused_for_lark__\"");
        handler.Requests[1].Body.Should().Contain("\"app_id\":\"cli_a1b2c3\"");
        handler.Requests[2].Body.Should().Contain("\"default_agent\":true");
    }

    [Theory]
    [InlineData("", "cli_a1b2c3", "secret-xyz", "https://aevatar.example.com", "missing_access_token")]
    [InlineData("user-token", "", "secret-xyz", "https://aevatar.example.com", "missing_app_id")]
    [InlineData("user-token", "cli_a1b2c3", "", "https://aevatar.example.com", "missing_app_secret")]
    [InlineData("user-token", "cli_a1b2c3", "secret-xyz", "", "missing_webhook_base_url")]
    public async Task ProvisionAsync_ShouldRejectInvalidRequests_BeforeCallingNyx(
        string accessToken,
        string appId,
        string appSecret,
        string webhookBaseUrl,
        string expectedError)
    {
        var handler = new RecordingHandler();
        var service = CreateService(handler);

        var result = await service.ProvisionAsync(
            new NyxLarkProvisioningRequest(
                AccessToken: accessToken,
                AppId: appId,
                AppSecret: appSecret,
                WebhookBaseUrl: webhookBaseUrl,
                ScopeId: "scope-1",
                Label: "Ops Bot",
                NyxProviderSlug: "api-lark-bot"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Status.Should().Be("error");
        result.Error.Should().Be(expectedError);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvisionAsync_ShouldReject_WhenNyxBaseUrlIsNotConfigured()
    {
        var handler = new RecordingHandler();
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions(), new HttpClient(handler));
        var actorRuntime = Substitute.For<IActorRuntime>();
        var service = new NyxLarkProvisioningService(
            nyxClient,
            new NyxIdToolOptions(),
            actorRuntime,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("nyx_base_url_not_configured");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvisionAsync_ShouldRollbackRemoteResources_WhenLocalMirrorRegistrationFails()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", """{"id":"key-123"}""");
        handler.Enqueue("/api/v1/channel-bots", """{"id":"bot-456"}""");
        handler.Enqueue("/api/v1/channel-conversations", """{"id":"route-789"}""");
        handler.Enqueue(HttpMethod.Delete, "/api/v1/channel-conversations/route-789", """{"ok":true}""");
        handler.Enqueue(HttpMethod.Delete, "/api/v1/channel-bots/bot-456", """{"ok":true}""");
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-123", """{"ok":true}""");

        var actor = Substitute.For<IActor>();
        actor.Id.Returns(ChannelBotRegistrationGAgent.WellKnownId);
        actor.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("mirror failed"));

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(actor));

        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            actorRuntime,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("mirror failed");
        handler.Requests.Should().HaveCount(6);
        handler.Requests[3].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[3].Path.Should().Be("/api/v1/channel-conversations/route-789");
        handler.Requests[4].Path.Should().Be("/api/v1/channel-bots/bot-456");
        handler.Requests[5].Path.Should().Be("/api/v1/api-keys/key-123");
    }

    private static bool MatchesLocalMirror(ChannelBotRegisterCommand command, string registrationId) =>
        command.RequestedId == registrationId &&
        command.Platform == "lark" &&
        command.NyxProviderSlug == "api-lark-bot" &&
        command.NyxUserToken == string.Empty &&
        command.NyxRefreshToken == string.Empty &&
        command.VerificationToken == string.Empty &&
        command.CredentialRef == string.Empty &&
        command.NyxAgentApiKeyId == "key-123" &&
        command.NyxChannelBotId == "bot-456" &&
        command.NyxConversationRouteId == "route-789" &&
        command.WebhookUrl == "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-456";

    private static NyxLarkProvisioningRequest BuildRequest() =>
        new(
            AccessToken: "user-token",
            AppId: "cli_a1b2c3",
            AppSecret: "secret-xyz",
            WebhookBaseUrl: "https://aevatar.example.com",
            ScopeId: "scope-1",
            Label: "Ops Bot",
            NyxProviderSlug: "api-lark-bot");

    private static NyxLarkProvisioningService CreateService(RecordingHandler handler)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));

        var actorRuntime = Substitute.For<IActorRuntime>();
        return new NyxLarkProvisioningService(
            nyxClient,
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            actorRuntime,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpMethod? Method, string Path, string Body)> _responses = new();

        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

        public void Enqueue(string path, string body) => _responses.Enqueue((null, path, body));

        public void Enqueue(HttpMethod method, string path, string body) => _responses.Enqueue((method, path, body));

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
