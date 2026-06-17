using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class NyxLarkProvisioningServiceTests
{
    [Fact]
    public async Task ProvisionAsync_Captures_Nyx_FullKey_And_Dispatches_Local_Mirror_With_Opaque_Ref()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", """{"id":"key-123","full_key":"full-key"}""");
        handler.Enqueue("/api/v1/channel-bots", """{"id":"bot-456","status":"pending_webhook"}""");
        handler.Enqueue("/api/v1/channel-conversations", """{"id":"route-789","default_agent":true}""");
        handler.Enqueue("/api/v1/keys", """{"id":"svc-1"}""");

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
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);
        var commandFacade = ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime);
        var secretsStore = new RecordingSecretsStore();

        var service = new NyxLarkProvisioningService(
            nyxClient,
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            commandFacade,
            secretsStore,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(
            new NyxLarkProvisioningRequest(
                AccessToken: "user-token",
                AppId: "cli_a1b2c3",
                AppSecret: "secret-xyz",
                VerificationToken: "verify-123",
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
        result.NyxReplyCredentialRef.Should().Be($"secrets://channel/nyxid/lark/{result.RegistrationId}/reply-api-key");
        result.RelayCallbackUrl.Should().Be("https://aevatar.example.com/api/webhooks/nyxid-relay");
        result.WebhookUrl.Should().Be("https://nyx.example.com/api/v1/webhooks/channel/lark/bot-456");
        secretsStore.Get(result.NyxReplyCredentialRef!).Should().Be("full-key");

        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Is(ChannelBotRegisterCommand.Descriptor).Should().BeTrue();
        MatchesLocalMirror(capturedEnvelope.Payload.Unpack<ChannelBotRegisterCommand>(), result.RegistrationId!)
            .Should().BeTrue();
        capturedEnvelope.Payload.Unpack<ChannelBotRegisterCommand>().NyxReplyCredentialRef.Should().Be(result.NyxReplyCredentialRef);
        capturedEnvelope.Payload.Unpack<ChannelBotRegisterCommand>().ToString().Should().NotContain("full-key");

        handler.Requests.Should().HaveCount(4);
        handler.Requests[0].Body.Should().Contain("\"callback_url\":\"https://aevatar.example.com/api/webhooks/nyxid-relay\"");
        handler.Requests[0].Body.Should().Contain("\"platform\":\"generic\"");
        handler.Requests[1].Body.Should().Contain("\"bot_token\":\"__unused_for_lark__\"");
        handler.Requests[1].Body.Should().Contain("\"app_id\":\"cli_a1b2c3\"");
        handler.Requests[1].Body.Should().Contain("\"verification_token\":\"verify-123\"");
        handler.Requests[2].Body.Should().Contain("\"default_agent\":true");
        handler.Requests[3].Body.Should().Contain("\"label\":\"Lark App cli_a1b2c3\"");
        handler.Requests[3].Body.Should().Contain("\"service_slug\":\"api-lark-bot\"");
    }

    [Theory]
    [InlineData("", "cli_a1b2c3", "secret-xyz", "https://aevatar.example.com", "scope-1", "missing_access_token")]
    [InlineData("user-token", "", "secret-xyz", "https://aevatar.example.com", "scope-1", "missing_app_id")]
    [InlineData("user-token", "cli_a1b2c3", "", "https://aevatar.example.com", "scope-1", "missing_app_secret")]
    [InlineData("user-token", "cli_a1b2c3", "secret-xyz", "", "scope-1", "missing_webhook_base_url")]
    [InlineData("user-token", "cli_a1b2c3", "secret-xyz", "https://aevatar.example.com", "", "missing_scope_id")]
    public async Task ProvisionAsync_ShouldRejectInvalidRequests_BeforeCallingNyx(
        string accessToken,
        string appId,
        string appSecret,
        string webhookBaseUrl,
        string scopeId,
        string expectedError)
    {
        var handler = new RecordingHandler();
        var service = CreateService(handler);

        var result = await service.ProvisionAsync(
            new NyxLarkProvisioningRequest(
                AccessToken: accessToken,
                AppId: appId,
                AppSecret: appSecret,
                VerificationToken: string.Empty,
                WebhookBaseUrl: webhookBaseUrl,
                ScopeId: scopeId,
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
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var service = new NyxLarkProvisioningService(
            nyxClient,
            new NyxIdToolOptions(),
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            new RecordingSecretsStore(),
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
        handler.Enqueue("/api/v1/api-keys", """{"id":"key-123","full_key":"full-key"}""");
        handler.Enqueue("/api/v1/channel-bots", """{"id":"bot-456"}""");
        handler.Enqueue("/api/v1/channel-conversations", """{"id":"route-789"}""");
        handler.Enqueue("/api/v1/keys", """{"id":"svc-1"}""");
        handler.Enqueue(HttpMethod.Delete, "/api/v1/channel-conversations/route-789", """{"ok":true}""");
        handler.Enqueue(HttpMethod.Delete, "/api/v1/channel-bots/bot-456", """{"ok":true}""");
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-123", """{"ok":true}""");

        var actor = Substitute.For<IActor>();
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(actor));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Any<EventEnvelope>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<DispatchAdmission>(new InvalidOperationException("mirror failed")));
        var commandFacade = ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime);

        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            commandFacade,
            new RecordingSecretsStore(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("mirror failed");
        handler.Requests.Should().HaveCount(7);
        handler.Requests[3].Path.Should().Be("/api/v1/keys");
        handler.Requests[4].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[4].Path.Should().Be("/api/v1/channel-conversations/route-789");
        handler.Requests[5].Path.Should().Be("/api/v1/channel-bots/bot-456");
        handler.Requests[6].Path.Should().Be("/api/v1/api-keys/key-123");
    }

    [Fact]
    public async Task ProvisionAsync_ShouldRollbackApiKey_WhenFullKeyIsMissing()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", """{"id":"key-123"}""");
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-123", """{"ok":true}""");

        var secretsStore = new RecordingSecretsStore();
        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(
                Substitute.For<IActorRuntime, IActorDispatchPort>(),
                Substitute.For<IActorDispatchPort>()),
            secretsStore,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("missing_full_key_in_api_key_id_response");
        secretsStore.GetAll().Should().BeEmpty();
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[1].Path.Should().Be("/api/v1/api-keys/key-123");
    }

    private static bool MatchesLocalMirror(ChannelBotRegisterCommand command, string registrationId) =>
        command.RequestedId == registrationId &&
        command.Platform == "lark" &&
        command.NyxProviderSlug == "api-lark-bot" &&
        command.ScopeId == "scope-1" &&
        command.NyxAgentApiKeyId == "key-123" &&
        command.NyxChannelBotId == "bot-456" &&
        command.NyxConversationRouteId == "route-789" &&
        command.NyxReplyCredentialRef == $"secrets://channel/nyxid/lark/{registrationId}/reply-api-key" &&
        command.WebhookUrl == "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-456";

    private static NyxLarkProvisioningRequest BuildRequest() =>
        new(
            AccessToken: "user-token",
            AppId: "cli_a1b2c3",
            AppSecret: "secret-xyz",
            VerificationToken: string.Empty,
            WebhookBaseUrl: "https://aevatar.example.com",
            ScopeId: "scope-1",
            Label: "Ops Bot",
            NyxProviderSlug: "api-lark-bot");

    private static NyxLarkProvisioningService CreateService(RecordingHandler handler)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        return new NyxLarkProvisioningService(
            nyxClient,
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            new RecordingSecretsStore(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());
    }

    private sealed class RecordingSecretsStore : IAevatarSecretsStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public string? Get(string key) => _secrets.GetValueOrDefault(key);

        public string? GetApiKey(string providerName) => null;

        public string? GetDefaultProvider() => null;

        public IReadOnlyDictionary<string, string> GetAll() => _secrets;

        public void Set(string key, string value) => _secrets[key] = value;

        public void Remove(string key) => _secrets.Remove(key);
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
