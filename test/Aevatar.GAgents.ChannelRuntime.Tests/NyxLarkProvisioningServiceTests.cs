using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using FluentAssertions;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class NyxLarkProvisioningServiceTests
{
    [Fact]
    public async Task ProvisionAsync_Captures_FullKey_Into_Vault_And_Mirrors_Typed_Handle_Only()
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

        var secretVault = new InMemorySecretVault();
        var service = new NyxLarkProvisioningService(
            nyxClient,
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            commandFacade,
            secretVault,
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
                NyxProviderSlug: "api-lark-bot",
                EncryptKey: " encrypt-alpha "),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Status.Should().Be("accepted");
        result.RegistrationId.Should().NotBeNullOrWhiteSpace();
        result.NyxAgentApiKeyId.Should().Be("key-123");
        result.NyxChannelBotId.Should().Be("bot-456");
        result.NyxConversationRouteId.Should().Be("route-789");
        result.WorkflowResultDeliveryEnabled.Should().BeTrue();
        result.RelayCallbackUrl.Should().Be("https://aevatar.example.com/api/webhooks/nyxid-relay");
        result.WebhookUrl.Should().Be("https://nyx.example.com/api/v1/webhooks/channel/lark/bot-456");

        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Is(ChannelBotRegisterCommand.Descriptor).Should().BeTrue();
        var command = capturedEnvelope.Payload.Unpack<ChannelBotRegisterCommand>();
        MatchesLocalMirror(command, result.RegistrationId!).Should().BeTrue();
        // The one-time full_key lives ONLY in the vault; the mirror command carries the typed handle.
        command.ToString().Should().NotContain("full-key");
        command.WorkflowResultDeliveryCredential.Should().NotBeNull();
        command.WorkflowResultDeliveryCredential.Purpose
            .Should().Be(CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey);
        command.WorkflowResultDeliveryCredential.OwnerScopeKey.Should().Be("scope-1");
        var resolved = await secretVault.ResolveAsync(new ResolveSecretRequest(
            command.WorkflowResultDeliveryCredential.Ref,
            CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            "scope-1",
            "key-123",
            "test-read"));
        resolved.Resolved.Should().BeTrue();
        resolved.Secret.Should().Be("full-key");

        handler.Requests.Should().HaveCount(4);
        handler.Requests[0].Body.Should().Contain("\"callback_url\":\"https://aevatar.example.com/api/webhooks/nyxid-relay\"");
        handler.Requests[0].Body.Should().Contain("\"platform\":\"generic\"");
        handler.Requests[1].Body.Should().Contain("\"bot_token\":\"__unused_for_lark__\"");
        handler.Requests[1].Body.Should().Contain("\"app_id\":\"cli_a1b2c3\"");
        handler.Requests[1].Body.Should().Contain("\"verification_token\":\"verify-123\"");
        handler.Requests[1].Body.Should().Contain("\"encrypt_key\":\"encrypt-alpha\"");
        capturedEnvelope.ToString().Should().NotContain("encrypt-alpha");
        handler.Requests[2].Body.Should().Contain("\"default_agent\":true");
        handler.Requests[3].Body.Should().Contain("\"label\":\"Lark App cli_a1b2c3\"");
        handler.Requests[3].Body.Should().Contain("\"service_slug\":\"api-lark-bot\"");
    }

    [Fact]
    public async Task ProvisionAsync_Without_FullKey_Provisions_Bot_Without_Delivery_Credential()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", """{"id":"key-123"}""");
        handler.Enqueue("/api/v1/channel-bots", """{"id":"bot-456","status":"pending_webhook"}""");
        handler.Enqueue("/api/v1/channel-conversations", """{"id":"route-789","default_agent":true}""");
        handler.Enqueue("/api/v1/keys", """{"id":"svc-1"}""");

        EventEnvelope? capturedEnvelope = null;
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            new InMemorySecretVault(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.WorkflowResultDeliveryEnabled.Should().BeFalse();
        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Unpack<ChannelBotRegisterCommand>()
            .WorkflowResultDeliveryCredential.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionAsync_Omits_Blank_EncryptKey_From_ChannelBot_Payload()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", """{"id":"key-123","full_key":"full-key"}""");
        handler.Enqueue("/api/v1/channel-bots", """{"id":"bot-456"}""");
        handler.Enqueue("/api/v1/channel-conversations", """{"id":"route-789"}""");
        handler.Enqueue("/api/v1/keys", """{"id":"svc-1"}""");

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Any<EventEnvelope>(),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);
        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            new InMemorySecretVault(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(
            BuildRequest() with { EncryptKey = "   " },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        handler.Requests[1].Body.Should().NotContain("encrypt_key");
    }

    [Fact]
    public async Task ProvisionAsync_Rejects_Cleartext_Http_WebhookBaseUrl_Before_Calling_Nyx()
    {
        // The relay callback URL receives the short-lived X-NyxID-User-Token; registering a
        // cleartext http:// callback would ship that first-party bearer over the wire in the clear.
        var handler = new RecordingHandler();
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var commandFacade = ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime);
        var service = new NyxLarkProvisioningService(
            nyxClient,
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            commandFacade,
            new InMemorySecretVault(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(
            new NyxLarkProvisioningRequest(
                AccessToken: "user-token",
                AppId: "cli_a1b2c3",
                AppSecret: "secret-xyz",
                VerificationToken: "verify-123",
                WebhookBaseUrl: "http://aevatar.example.com",
                ScopeId: "scope-1",
                Label: "Ops Bot",
                NyxProviderSlug: "api-lark-bot"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("insecure_webhook_base_url");
        // Guard must fail closed BEFORE any NyxID provisioning call is made.
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvisionAsync_Stores_NyxAssigned_PerConnection_Slug_When_Connect_Returns_Suffixed_Slug()
    {
        // Regression: a user's 2nd+ Lark bot. NyxID auto-numbers the proxy slug when `api-lark-bot`
        // is already taken (here api-lark-bot-3) and returns it on `POST /api/v1/keys`. The mirror
        // must store that per-connection slug so this bot replies through ITS OWN Lark app instead
        // of the first one (the multi-bot cross-talk bug).
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", """{"id":"key-123","full_key":"full-key"}""");
        handler.Enqueue("/api/v1/channel-bots", """{"id":"bot-456","status":"pending_webhook"}""");
        handler.Enqueue("/api/v1/channel-conversations", """{"id":"route-789","default_agent":true}""");
        handler.Enqueue("/api/v1/keys", """{"id":"svc-3","proxy_url_slug":"https://nyx.example.com/api/v1/proxy/s/api-lark-bot-3/{path}"}""");

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

        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            commandFacade,
            new InMemorySecretVault(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Unpack<ChannelBotRegisterCommand>().NyxProviderSlug
            .Should().Be("api-lark-bot-3");
    }

    [Fact]
    public async Task ProvisionAsync_Uses_Explicit_ProviderSlug_For_Proxy_Service_Connection()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", """{"id":"key-123","full_key":"full-key"}""");
        handler.Enqueue("/api/v1/channel-bots", """{"id":"bot-456","status":"pending_webhook"}""");
        handler.Enqueue("/api/v1/channel-conversations", """{"id":"route-789","default_agent":true}""");
        handler.Enqueue("/api/v1/keys", """{"id":"svc-explicit","slug":"api-lark-bot-custom"}""");

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

        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            commandFacade,
            new InMemorySecretVault(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(
            BuildRequest() with { NyxProviderSlug = " api-lark-bot-custom " },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        handler.Requests[3].Path.Should().Be("/api/v1/keys");
        handler.Requests[3].Body.Should().Contain("\"service_slug\":\"api-lark-bot-custom\"");
        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Unpack<ChannelBotRegisterCommand>().NyxProviderSlug
            .Should().Be("api-lark-bot-custom");
    }

    [Theory]
    [InlineData("", "cli_a1b2c3", "secret-xyz", "verify-123", "https://aevatar.example.com", "scope-1", "missing_access_token")]
    [InlineData("user-token", "", "secret-xyz", "verify-123", "https://aevatar.example.com", "scope-1", "missing_app_id")]
    [InlineData("user-token", "cli_a1b2c3", "", "verify-123", "https://aevatar.example.com", "scope-1", "missing_app_secret")]
    [InlineData("user-token", "cli_a1b2c3", "secret-xyz", "", "https://aevatar.example.com", "scope-1", "missing_verification_token")]
    [InlineData("user-token", "cli_a1b2c3", "secret-xyz", "verify-123", "", "scope-1", "missing_webhook_base_url")]
    [InlineData("user-token", "cli_a1b2c3", "secret-xyz", "verify-123", "https://aevatar.example.com", "", "missing_scope_id")]
    public async Task ProvisionAsync_ShouldRejectInvalidRequests_BeforeCallingNyx(
        string accessToken,
        string appId,
        string appSecret,
        string verificationToken,
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
                VerificationToken: verificationToken,
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
    public async Task ProvisionAsync_Replaces_Existing_ChannelBot_On_409_Conflict_And_Retries()
    {
        // Re-binding the same Lark app: the first channel-bot create hits NyxID's 409 already-exists.
        // The REAL `GET /api/v1/channel-bots` list omits `platform_bot_id` (it lives only on the
        // per-bot detail), so the service lists Lark bots, GETs each one's detail, and deletes only
        // the bot whose detail `platform_bot_id` equals THIS app, then retries — so a user can re-bind
        // from /channels without manual NyxID cleanup (the 502 the wizard showed). A second Lark bot
        // for a DIFFERENT app is fetched but left untouched.
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-1","full_key":"x"}""");
        handler.Enqueue(HttpMethod.Post, "/api/v1/channel-bots", """{"error":true,"status":409,"message":"channel bot already exists"}""");
        // Real list shape: items have id + platform, NO platform_bot_id. Includes a telegram bot
        // (skipped) and a second lark bot for another app (fetched, not matched, not deleted).
        handler.Enqueue(HttpMethod.Get, "/api/v1/channel-bots",
            """{"bots":[{"id":"old-bot","platform":"lark","platform_bot_username":"lark_bot","status":"active"},{"id":"tg-bot","platform":"telegram","status":"active"},{"id":"other-lark","platform":"lark","platform_bot_username":"lark_bot","status":"active"}],"total":3}""");
        // Real detail shape: WITH platform_bot_id. First lark bot matches this app; second does not.
        handler.Enqueue(HttpMethod.Get, "/api/v1/channel-bots/old-bot", """{"id":"old-bot","platform":"lark","platform_bot_id":"cli_a1b2c3","status":"active"}""");
        handler.Enqueue(HttpMethod.Delete, "/api/v1/channel-bots/old-bot", """{"ok":true}""");
        handler.Enqueue(HttpMethod.Get, "/api/v1/channel-bots/other-lark", """{"id":"other-lark","platform":"lark","platform_bot_id":"cli_zzz","status":"active"}""");
        handler.Enqueue(HttpMethod.Post, "/api/v1/channel-bots", """{"id":"bot-new"}""");
        handler.Enqueue(HttpMethod.Post, "/api/v1/channel-conversations", """{"id":"route-1"}""");
        handler.Enqueue(HttpMethod.Post, "/api/v1/keys", """{"id":"svc-1","slug":"api-lark-bot-2"}""");

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Any<EventEnvelope>(),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            new InMemorySecretVault(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.NyxChannelBotId.Should().Be("bot-new");
        // Only the channel-bot for THIS Lark app (resolved via detail) was deleted before the retry.
        handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Delete && r.Path.StartsWith("/api/v1/channel-bots/"))
            .Which.Path.Should().Be("/api/v1/channel-bots/old-bot");
        // The detail of the other-app Lark bot was read but it was never deleted.
        handler.Requests.Should().Contain(r => r.Method == HttpMethod.Get && r.Path == "/api/v1/channel-bots/other-lark");
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete && r.Path == "/api/v1/channel-bots/other-lark");
    }

    [Fact]
    public async Task ProvisionAsync_ShouldReject_WhenNyxBaseUrlIsNotConfigured()
    {
        var handler = new RecordingHandler();
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = null }, new HttpClient(handler));
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var service = new NyxLarkProvisioningService(
            nyxClient,
            new NyxIdToolOptions { BaseUrl = null },
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            new InMemorySecretVault(),
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

        var secretVault = new RecordingSecretVault();
        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            commandFacade,
            secretVault,
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
        // The vault-side compensation revokes the just-stored delivery credential with the
        // same ref/purpose/subject that the put minted.
        secretVault.PutRequests.Should().ContainSingle();
        secretVault.RevokeRequests.Should().ContainSingle();
        secretVault.RevokeRequests[0].Ref.Should().Be(secretVault.StoredReferences.Single().Ref);
        secretVault.RevokeRequests[0].Purpose.Should().Be(CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey);
        secretVault.RevokeRequests[0].SubjectId.Should().Be("key-123");
    }

    [Fact]
    public async Task ProvisionAsync_ShouldRevokeVaultCredentialAndDeleteApiKey_WhenNyxStepFailsAfterVaultPut()
    {
        // The vault put happens right after the api-key create; a later NyxID step failing must
        // compensate BOTH legs — revoke the vault record and delete the NyxID api key — or a live
        // key stays resolvable with no registration referencing it.
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", """{"id":"key-123","full_key":"full-key"}""");
        handler.Enqueue("/api/v1/channel-bots", """{"error":true,"status":500,"message":"upstream unavailable"}""");
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-123", """{"ok":true}""");

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var secretVault = new RecordingSecretVault();
        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            secretVault,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        handler.Requests.Should().HaveCount(3);
        handler.Requests[2].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[2].Path.Should().Be("/api/v1/api-keys/key-123");
        secretVault.RevokeRequests.Should().ContainSingle();
        secretVault.RevokeRequests[0].Ref.Should().Be(secretVault.StoredReferences.Single().Ref);
        secretVault.RevokeRequests[0].SubjectId.Should().Be("key-123");
    }

    [Fact]
    public async Task ProvisionAsync_ShouldStillDeleteApiKey_WhenVaultRevokeFails()
    {
        // The production Garnet vault revoke can fail (backend unavailable, CAS conflict); the
        // failure must not shadow the api-key delete that follows it — deleting the NyxID key is
        // what makes an orphaned vault record inert.
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", """{"id":"key-123","full_key":"full-key"}""");
        handler.Enqueue("/api/v1/channel-bots", """{"error":true,"status":500,"message":"upstream unavailable"}""");
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-123", """{"ok":true}""");

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var secretVault = new RecordingSecretVault
        {
            RevokeException = new InvalidOperationException(
                "Garnet secret vault rotate/revoke requires atomic versioned transitions."),
        };
        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            secretVault,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        // The original NyxID failure surfaces, not the revoke failure.
        result.Error.Should().StartWith("channel_bot_id_request_failed");
        secretVault.RevokeRequests.Should().ContainSingle();
        handler.Requests.Should().HaveCount(3);
        handler.Requests[2].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[2].Path.Should().Be("/api/v1/api-keys/key-123");
    }

    [Fact]
    public async Task ProvisionAsync_ShouldCompensateRemoteAndVaultState_WhenCancelledAfterVaultPut()
    {
        // When the triggering failure IS the caller's cancellation, compensation must still run:
        // reusing the cancelled token would abort every rollback delete and leak a live NyxID api
        // key whose full_key stays resolvable in the vault.
        using var cts = new CancellationTokenSource();
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", """{"id":"key-123","full_key":"full-key"}""");
        handler.Enqueue(HttpMethod.Post, "/api/v1/channel-bots", () =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        });
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-123", """{"ok":true}""");

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var secretVault = new RecordingSecretVault();
        var service = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            secretVault,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var result = await service.ProvisionAsync(BuildRequest(), cts.Token);

        result.Succeeded.Should().BeFalse();
        secretVault.RevokeRequests.Should().ContainSingle();
        handler.Requests.Should().HaveCount(3);
        handler.Requests[2].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[2].Path.Should().Be("/api/v1/api-keys/key-123");
    }

    private static bool MatchesLocalMirror(ChannelBotRegisterCommand command, string registrationId) =>
        command.RequestedId == registrationId &&
        command.Platform == "lark" &&
        command.NyxProviderSlug == "api-lark-bot" &&
        command.ScopeId == "scope-1" &&
        command.NyxAgentApiKeyId == "key-123" &&
        command.NyxChannelBotId == "bot-456" &&
        command.NyxConversationRouteId == "route-789" &&
        command.WebhookUrl == "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-456";

    private static NyxLarkProvisioningRequest BuildRequest() =>
        new(
            AccessToken: "user-token",
            AppId: "cli_a1b2c3",
            AppSecret: "secret-xyz",
            VerificationToken: "verify-123",
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
            new InMemorySecretVault(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());
    }

    private sealed class RecordingSecretVault : ISecretVault
    {
        private readonly InMemorySecretVault _inner = new();

        public List<StoreSecretRequest> PutRequests { get; } = [];
        public List<RevokeSecretRequest> RevokeRequests { get; } = [];
        public List<SecretReference> StoredReferences { get; } = [];
        public Exception? RevokeException { get; init; }

        public async Task<StoreSecretResult> PutAsync(StoreSecretRequest request, CancellationToken ct = default)
        {
            PutRequests.Add(request);
            var result = await _inner.PutAsync(request, ct);
            StoredReferences.Add(result.Reference);
            return result;
        }

        public Task<ResolveSecretResult> ResolveAsync(ResolveSecretRequest request, CancellationToken ct = default) =>
            _inner.ResolveAsync(request, ct);

        public Task<RotateSecretResult> RotateAsync(RotateSecretRequest request, CancellationToken ct = default) =>
            _inner.RotateAsync(request, ct);

        public Task<RevokeSecretResult> RevokeAsync(RevokeSecretRequest request, CancellationToken ct = default)
        {
            RevokeRequests.Add(request);
            return RevokeException is null
                ? _inner.RevokeAsync(request, ct)
                : Task.FromException<RevokeSecretResult>(RevokeException);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpMethod? Method, string Path, string Body, Func<Exception>? ExceptionFactory)> _responses = new();

        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

        public void Enqueue(string path, string body) => _responses.Enqueue((null, path, body, null));

        public void Enqueue(HttpMethod method, string path, string body) => _responses.Enqueue((method, path, body, null));

        public void Enqueue(HttpMethod method, string path, Func<Exception> exceptionFactory) =>
            _responses.Enqueue((method, path, string.Empty, exceptionFactory));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // A real socket handler aborts a request whose token is already cancelled; without
            // this check the cancellation-compensation test passes even when rollback deletes
            // reuse the cancelled caller token.
            cancellationToken.ThrowIfCancellationRequested();

            if (_responses.Count == 0)
                throw new InvalidOperationException("No more queued responses.");

            var (expectedMethod, expectedPath, responseBody, exceptionFactory) = _responses.Dequeue();
            request.RequestUri.Should().NotBeNull();
            request.RequestUri!.AbsolutePath.Should().Be(expectedPath);
            if (expectedMethod is not null)
                request.Method.Should().Be(expectedMethod);

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, expectedPath, body));

            if (exceptionFactory is not null)
                throw exceptionFactory();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
