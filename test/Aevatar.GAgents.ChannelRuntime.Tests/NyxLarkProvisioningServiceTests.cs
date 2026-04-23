using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Configuration;
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
        var secretsStore = new InMemorySecretsStore();

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

        var service = new NyxLarkProvisioningService(
            nyxClient,
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            actorRuntime,
            (IActorDispatchPort)actorRuntime,
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
        result.CredentialRef.Should().Be($"vault://channels/lark/registrations/{result.RegistrationId}/relay-hmac");
        result.NyxChannelBotId.Should().Be("bot-456");
        result.NyxConversationRouteId.Should().Be("route-789");
        result.RelayCallbackUrl.Should().Be("https://aevatar.example.com/api/webhooks/nyxid-relay");
        result.WebhookUrl.Should().Be("https://nyx.example.com/api/v1/webhooks/channel/lark/bot-456");
        secretsStore.Get(result.CredentialRef!).Should().Be(ComputeHash("full-key"));

        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Is(ChannelBotRegisterCommand.Descriptor).Should().BeTrue();
        MatchesLocalMirror(capturedEnvelope.Payload.Unpack<ChannelBotRegisterCommand>(), result.RegistrationId!, result.CredentialRef!)
            .Should().BeTrue();

        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].Body.Should().Contain("\"callback_url\":\"https://aevatar.example.com/api/webhooks/nyxid-relay\"");
        handler.Requests[0].Body.Should().Contain("\"platform\":\"generic\"");
        handler.Requests[1].Body.Should().Contain("\"bot_token\":\"__unused_for_lark__\"");
        handler.Requests[1].Body.Should().Contain("\"app_id\":\"cli_a1b2c3\"");
        handler.Requests[1].Body.Should().Contain("\"verification_token\":\"verify-123\"");
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
                VerificationToken: string.Empty,
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
        var secretsStore = new InMemorySecretsStore();
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions(), new HttpClient(handler));
        var actorRuntime = Substitute.For<IActorRuntime>();
        var service = new NyxLarkProvisioningService(
            nyxClient,
            new NyxIdToolOptions(),
            actorRuntime,
            Substitute.For<IActorDispatchPort>(),
            secretsStore,
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
        handler.Enqueue(HttpMethod.Delete, "/api/v1/channel-conversations/route-789", """{"ok":true}""");
        handler.Enqueue(HttpMethod.Delete, "/api/v1/channel-bots/bot-456", """{"ok":true}""");
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-123", """{"ok":true}""");
        var secretsStore = new InMemorySecretsStore();

        var actor = Substitute.For<IActor>();
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(actor));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Any<EventEnvelope>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("mirror failed"));

        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            actorRuntime,
            (IActorDispatchPort)actorRuntime,
            secretsStore,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("mirror failed");
        handler.Requests.Should().HaveCount(6);
        handler.Requests[3].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[3].Path.Should().Be("/api/v1/channel-conversations/route-789");
        handler.Requests[4].Path.Should().Be("/api/v1/channel-bots/bot-456");
        handler.Requests[5].Path.Should().Be("/api/v1/api-keys/key-123");
        secretsStore.GetAll().Should().BeEmpty();
    }

    [Fact]
    public async Task RepairLocalMirrorAsync_ReusesExistingNyxResources_AndDispatchesLocalMirror()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Get, "/api/v1/api-keys/key-123", """{"id":"key-123","callback_url":"https://aevatar.example.com/api/webhooks/nyxid-relay"}""");
        handler.Enqueue(HttpMethod.Get, "/api/v1/channel-bots/bot-456", """{"id":"bot-456","platform":"lark","webhook_url":"https://nyx.example.com/api/v1/webhooks/channel/lark/bot-456"}""");
        handler.Enqueue(HttpMethod.Get, "/api/v1/channel-conversations/route-789", """{"id":"route-789","channel_bot_id":"bot-456","agent_api_key_id":"key-123","default_agent":true}""");
        var secretsStore = new InMemorySecretsStore();
        secretsStore.Set("vault://channels/lark/registrations/reg-restore-1/relay-hmac", "hashed-secret");

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

        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            actorRuntime,
            (IActorDispatchPort)actorRuntime,
            secretsStore,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.RepairLocalMirrorAsync(
            new NyxLarkMirrorRepairRequest(
                AccessToken: "user-token",
                RequestedRegistrationId: "reg-restore-1",
                ScopeId: "scope-1",
                NyxProviderSlug: "api-lark-bot",
                WebhookBaseUrl: "https://aevatar.example.com",
                NyxChannelBotId: "bot-456",
                NyxAgentApiKeyId: "key-123",
                NyxConversationRouteId: "route-789",
                CredentialRef: string.Empty),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Status.Should().Be("accepted");
        result.RegistrationId.Should().Be("reg-restore-1");
        result.WebhookUrl.Should().Be("https://nyx.example.com/api/v1/webhooks/channel/lark/bot-456");
        handler.Requests.Should().HaveCount(3);

        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Is(ChannelBotRegisterCommand.Descriptor).Should().BeTrue();
        MatchesLocalMirror(
                capturedEnvelope.Payload.Unpack<ChannelBotRegisterCommand>(),
                "reg-restore-1",
                "vault://channels/lark/registrations/reg-restore-1/relay-hmac")
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task RepairLocalMirrorAsync_ShouldReject_WhenRelayCredentialCannotBeRecovered()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Get, "/api/v1/api-keys/key-123", """{"id":"key-123","callback_url":"https://aevatar.example.com/api/webhooks/nyxid-relay"}""");
        handler.Enqueue(HttpMethod.Get, "/api/v1/channel-bots/bot-456", """{"id":"bot-456","platform":"lark","webhook_url":"https://nyx.example.com/api/v1/webhooks/channel/lark/bot-456"}""");
        handler.Enqueue(HttpMethod.Get, "/api/v1/channel-conversations/route-789", """{"id":"route-789","channel_bot_id":"bot-456","agent_api_key_id":"key-123","default_agent":true}""");

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            actorRuntime,
            (IActorDispatchPort)actorRuntime,
            new InMemorySecretsStore(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.RepairLocalMirrorAsync(
            new NyxLarkMirrorRepairRequest(
                AccessToken: "user-token",
                RequestedRegistrationId: "reg-restore-1",
                ScopeId: "scope-1",
                NyxProviderSlug: "api-lark-bot",
                WebhookBaseUrl: "https://aevatar.example.com",
                NyxChannelBotId: "bot-456",
                NyxAgentApiKeyId: "key-123",
                NyxConversationRouteId: "route-789",
                CredentialRef: string.Empty),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("missing_relay_credential_ref");
        await ((IActorDispatchPort)actorRuntime).DidNotReceiveWithAnyArgs()
            .DispatchAsync(default!, default!, default);
    }

    [Fact]
    public async Task RepairLocalMirrorAsync_ShouldReject_WhenRelayApiKeyCallbackDoesNotMatchAevatarRelay()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Get, "/api/v1/api-keys/key-123", """{"id":"key-123","callback_url":"https://wrong.example.com/api/webhooks/nyxid-relay"}""");
        var secretsStore = new InMemorySecretsStore();
        secretsStore.Set("vault://channels/lark/registrations/reg-restore-1/relay-hmac", "hashed-secret");

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            actorRuntime,
            (IActorDispatchPort)actorRuntime,
            secretsStore,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.RepairLocalMirrorAsync(
            new NyxLarkMirrorRepairRequest(
                AccessToken: "user-token",
                RequestedRegistrationId: "reg-restore-1",
                ScopeId: "scope-1",
                NyxProviderSlug: "api-lark-bot",
                WebhookBaseUrl: "https://aevatar.example.com",
                NyxChannelBotId: "bot-456",
                NyxAgentApiKeyId: "key-123",
                NyxConversationRouteId: "route-789",
                CredentialRef: string.Empty),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("api_key_callback_url_mismatch");
        await ((IActorDispatchPort)actorRuntime).DidNotReceiveWithAnyArgs()
            .DispatchAsync(default!, default!, default);
    }

    [Fact]
    public async Task RepairLocalMirrorAsync_ShouldReject_WhenNoMatchingConversationRouteExistsInNyx()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Get, "/api/v1/api-keys/key-123", """{"id":"key-123","callback_url":"https://aevatar.example.com/api/webhooks/nyxid-relay"}""");
        handler.Enqueue(HttpMethod.Get, "/api/v1/channel-bots/bot-456", """{"id":"bot-456","platform":"lark","webhook_url":"https://nyx.example.com/api/v1/webhooks/channel/lark/bot-456"}""");
        handler.Enqueue(HttpMethod.Get, "/api/v1/channel-conversations", """{"routes":[]}""");
        var secretsStore = new InMemorySecretsStore();
        secretsStore.Set("vault://channels/lark/registrations/reg-restore-1/relay-hmac", "hashed-secret");

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));

        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            actorRuntime,
            (IActorDispatchPort)actorRuntime,
            secretsStore,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.RepairLocalMirrorAsync(
            new NyxLarkMirrorRepairRequest(
                AccessToken: "user-token",
                RequestedRegistrationId: "reg-restore-1",
                ScopeId: "scope-1",
                NyxProviderSlug: "api-lark-bot",
                WebhookBaseUrl: "https://aevatar.example.com",
                NyxChannelBotId: "bot-456",
                NyxAgentApiKeyId: "key-123",
                NyxConversationRouteId: string.Empty,
                CredentialRef: string.Empty),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("missing_matching_nyx_conversation_route");
        await ((IActorDispatchPort)actorRuntime).DidNotReceiveWithAnyArgs()
            .DispatchAsync(default!, default!, default);
    }

    private static bool MatchesLocalMirror(ChannelBotRegisterCommand command, string registrationId, string credentialRef) =>
        command.RequestedId == registrationId &&
        command.Platform == "lark" &&
        command.NyxProviderSlug == "api-lark-bot" &&
        command.ScopeId == "scope-1" &&
        command.NyxAgentApiKeyId == "key-123" &&
        command.CredentialRef == credentialRef &&
        command.NyxChannelBotId == "bot-456" &&
        command.NyxConversationRouteId == "route-789" &&
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
        return new NyxLarkProvisioningService(
            nyxClient,
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            actorRuntime,
            (IActorDispatchPort)actorRuntime,
            new InMemorySecretsStore(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());
    }

    private static string ComputeHash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class InMemorySecretsStore : IAevatarSecretsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.GetValueOrDefault(key);
        public string? GetApiKey(string providerName) => _values.GetValueOrDefault(providerName);
        public string? GetDefaultProvider() => null;
        public IReadOnlyDictionary<string, string> GetAll() => _values;
        public void Set(string key, string value) => _values[key] = value;
        public void Remove(string key) => _values.Remove(key);
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
