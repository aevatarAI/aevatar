using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class NyxTelegramProvisioningServiceTests
{
    [Fact]
    public async Task ProvisionAsync_UnverifiedOwner_RejectsBeforeNyxResources()
    {
        var handler = new RecordingHandler();
        var ownerResolver = Substitute.For<IChannelRegistrationOwnerResolver>();
        ownerResolver.ResolveAsync("user-token", "scope-1", Arg.Any<CancellationToken>())
            .Returns(new ChannelRegistrationOwnerResolution(null, "service_owner_forbidden"));
        var service = CreateService(handler, ownerResolver: ownerResolver);

        var result = await service.ProvisionAsync(BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("service_owner_forbidden");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvisionAsync_DefaultOrganizationOwner_SendsTargetOrganizationId()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", AgentKeyResponse("key-tg-1", "full-key"));
        handler.Enqueue("/api/v1/channel-bots", """{"id":"bot-tg-1"}""");
        handler.Enqueue("/api/v1/channel-conversations", """{"id":"route-tg-1"}""");
        var service = CreateService(
            handler,
            ownerResolver: OrganizationOwnerResolver("user-alpha", "org-alpha"));

        var result = await service.ProvisionAsync(
            BuildRequest() with { ScopeId = "org-alpha" },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        using var body = System.Text.Json.JsonDocument.Parse(handler.Requests[0].Body);
        body.RootElement.GetProperty("target_org_id").GetString().Should().Be("org-alpha");
    }

    [Fact]
    public async Task ProvisionAsync_creates_nyx_resources_and_dispatches_local_mirror()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", AgentKeyResponse("key-tg-1", "full-key"));
        handler.Enqueue("/api/v1/channel-bots", """{"id":"bot-tg-1","status":"pending_webhook"}""");
        handler.Enqueue("/api/v1/channel-conversations", """{"id":"route-tg-1","default_agent":true}""");

        var nyxOptions = new NyxIdToolOptions
        {
            BaseUrl = "http://nyxid.internal:3001",
            ApiBaseUrl = "https://nyx.example.com",
        };
        var nyxClient = new NyxIdApiClient(nyxOptions, new HttpClient(handler));

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
        var service = new NyxTelegramProvisioningService(
            nyxClient,
            nyxOptions,
            commandFacade,
            new ChannelAgentKeyProvisioningService(
                nyxClient,
                secretVault,
                NullLogger<ChannelAgentKeyProvisioningService>.Instance,
                ChannelAgentKeyWriteMode.NyxIdDefault),
            PersonalOwnerResolver("scope-1"),
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
        result.WorkflowResultDeliveryEnabled.Should().BeTrue();
        result.RelayCallbackUrl.Should().Be("https://aevatar.example.com/api/webhooks/nyxid-relay");
        result.WebhookUrl.Should().Be("https://nyx.example.com/api/v1/webhooks/channel/telegram/bot-tg-1");
        result.WebhookUrl.Should().NotContain("nyxid.internal");

        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Is(ChannelBotRegisterCommand.Descriptor).Should().BeTrue();
        var command = capturedEnvelope.Payload.Unpack<ChannelBotRegisterCommand>();
        command.Platform.Should().Be("telegram");
        command.NyxProviderSlug.Should().Be("api-telegram-bot");
        command.NyxAgentApiKeyId.Should().Be("key-tg-1");
        command.NyxChannelBotId.Should().Be("bot-tg-1");
        command.NyxConversationRouteId.Should().Be("route-tg-1");
        command.WebhookUrl.Should().Be("https://nyx.example.com/api/v1/webhooks/channel/telegram/bot-tg-1");
        command.AuthorizationMode.Should().Be(ChannelRegistrationAuthorizationMode.NyxidDefault);
        command.ChannelAgentKey.Should().NotBeNull();
        command.ChannelAgentKey.ApiKeyId.Should().Be("key-tg-1");
        command.ChannelAgentKey.Grant.HasAllowAllServices.Should().BeTrue();
        command.ChannelAgentKey.Grant.AllowAllServices.Should().BeTrue();
        command.ChannelAgentKey.Grant.HasAllowAllNodes.Should().BeTrue();
        command.ChannelAgentKey.Grant.AllowAllNodes.Should().BeTrue();
        command.NyxAgentApiKeyId.Should().Be(command.ChannelAgentKey.ApiKeyId);
        command.WorkflowResultDeliveryCredential.Should().Be(command.ChannelAgentKey.SecretReference);
        command.ToString().Should().NotContain("full-key");

        var resolved = await secretVault.ResolveAsync(new ResolveSecretRequest(
            command.ChannelAgentKey.SecretReference.Ref,
            CredentialSecretPurposes.ChannelNyxIdAgentKey,
            "scope-1",
            "key-tg-1",
            "test-read"));
        resolved.Resolved.Should().BeTrue();
        resolved.Secret.Should().Be("full-key");

        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].Body.Should().Contain("\"callback_url\":\"https://aevatar.example.com/api/webhooks/nyxid-relay\"");
        handler.Requests[0].Body.Should().Contain("\"platform\":\"generic\"");
        handler.Requests[0].Body.Should().Contain("\"scopes\":\"read write proxy\"");
        handler.Requests[0].Body.Should().NotContain("allowed_service_ids");
        handler.Requests[0].Body.Should().NotContain("allowed_node_ids");
        handler.Requests[0].Body.Should().NotContain("allow_all_services");
        handler.Requests[0].Body.Should().NotContain("allow_all_nodes");
        handler.Requests[0].Body.Should().NotContain("scope_plan_digest");
        handler.Requests[1].Body.Should().Contain("\"platform\":\"telegram\"");
        handler.Requests[1].Body.Should().Contain("\"bot_token\":\"1234567890:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef-hi\"");
        handler.Requests[1].Body.Should().NotContain("__unused_for_lark__");
        handler.Requests[2].Body.Should().Contain("\"default_agent\":true");
    }

    [Theory]
    [InlineData("", "bot-token", "https://aevatar.example.com", "scope-1", "missing_access_token")]
    [InlineData("user-token", "", "https://aevatar.example.com", "scope-1", "missing_bot_token")]
    [InlineData("user-token", "bot-token", "", "scope-1", "missing_webhook_base_url")]
    [InlineData("user-token", "bot-token", "http://aevatar.example.com", "scope-1", "insecure_webhook_base_url")]
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
    public async Task ProvisionAsync_surfaces_controlled_invalid_operation_message_but_not_dotnet_internals()
    {
        var handler = new RecordingHandler();
        // Non-JSON body in the api-keys response makes ExtractRequiredRelayApiKeyCredentials
        // throw InvalidOperationException with a structured controlled message. The catch in
        // ProvisionAsync routes that through SanitizeFailureReason — the controlled string is
        // safe to surface, but raw .NET stack/type internals must never leak.
        handler.Enqueue("/api/v1/api-keys", "not-json-at-all");

        var service = CreateService(handler);
        var result = await service.ProvisionAsync(
            new NyxTelegramProvisioningRequest(
                AccessToken: "user-token",
                BotToken: "1234567890:AA",
                WebhookBaseUrl: "https://aevatar.example.com",
                ScopeId: "scope-1",
                Label: "Ops Bot",
                NyxProviderSlug: "api-telegram-bot"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("channel_authorization_contract_invalid");
        result.Error.Should().NotContain("System.");
        result.Error.Should().NotContain("StackTrace");
    }

    [Fact]
    public async Task ProvisionAsync_WhenVaultPutFails_DeletesKeyBeforeBotRouteOrActorWrites()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", AgentKeyResponse("key-tg-1", "full-key"));
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-tg-1", """{"ok":true}""");
        var secretVault = new RecordingSecretVault
        {
            PutException = new InvalidOperationException("vault unavailable"),
        };
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var commandFacade = ChannelRegistrationCommandFacadeTestSupport.CreateFacade(
            actorRuntime,
            (IActorDispatchPort)actorRuntime);
        var service = CreateService(handler, secretVault: secretVault, commandFacade: commandFacade);

        var result = await service.ProvisionAsync(BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("secret_vault_unavailable");
        handler.Requests.Select(static request => (request.Method, request.Path)).Should().Equal(
            (HttpMethod.Post, "/api/v1/api-keys"),
            (HttpMethod.Delete, "/api/v1/api-keys/key-tg-1"));
        secretVault.PutRequests.Should().ContainSingle();
        await ((IActorDispatchPort)actorRuntime).DidNotReceiveWithAnyArgs()
            .DispatchAsync(default!, default!, default);
    }

    [Fact]
    public async Task ProvisionAsync_WhenLocalMirrorFails_CompensatesRouteBotKeyThenVault()
    {
        var effects = new List<string>();
        var handler = new RecordingHandler(effects);
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", AgentKeyResponse("key-tg-1", "full-key"));
        handler.Enqueue(HttpMethod.Post, "/api/v1/channel-bots", """{"id":"bot-tg-1"}""");
        handler.Enqueue(HttpMethod.Post, "/api/v1/channel-conversations", """{"id":"route-tg-1"}""");
        handler.Enqueue(HttpMethod.Delete, "/api/v1/channel-conversations/route-tg-1", """{"ok":true}""");
        handler.Enqueue(HttpMethod.Delete, "/api/v1/channel-bots/bot-tg-1", """{"ok":true}""");
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-tg-1", """{"ok":true}""");
        var secretVault = new RecordingSecretVault(effects);
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(null!));
        var service = CreateService(
            handler,
            secretVault: secretVault,
            commandFacade: ChannelRegistrationCommandFacadeTestSupport.CreateFacade(
                actorRuntime,
                (IActorDispatchPort)actorRuntime));

        var result = await service.ProvisionAsync(BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("local_mirror_dispatch_failed");
        effects.Should().Equal(
            "nyx:route-delete",
            "nyx:bot-delete",
            "nyx:key-delete",
            "vault:revoke");
    }

    [Fact]
    public async Task ProvisionAsync_WhenLocalMirrorAcceptanceIsUnknown_DoesNotCompensateExternalResources()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", AgentKeyResponse("key-tg-1", "full-key"));
        handler.Enqueue(HttpMethod.Post, "/api/v1/channel-bots", """{"id":"bot-tg-1"}""");
        handler.Enqueue(HttpMethod.Post, "/api/v1/channel-conversations", """{"id":"route-tg-1"}""");
        var secretVault = new RecordingSecretVault();
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Any<EventEnvelope>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<DispatchAdmission>(
                new OperationCanceledException("Admission acknowledgement was lost after dispatch started.")));
        var service = CreateService(
            handler,
            secretVault: secretVault,
            commandFacade: ChannelRegistrationCommandFacadeTestSupport.CreateFacade(
                actorRuntime,
                (IActorDispatchPort)actorRuntime));

        var result = await service.ProvisionAsync(BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("local_mirror_acceptance_unknown_remote_cleanup_skipped");
        handler.Requests.Should().HaveCount(3);
        handler.Requests.Should().OnlyContain(static request => request.Method != HttpMethod.Delete);
        secretVault.RevokeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvisionAsync_WhenCallerCancelsAfterVaultPut_UsesDetachedBoundedCleanupToken()
    {
        using var callerCts = new CancellationTokenSource();
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", AgentKeyResponse("key-tg-1", "full-key"));
        handler.Enqueue(HttpMethod.Post, "/api/v1/channel-bots", () =>
        {
            callerCts.Cancel();
            return new OperationCanceledException(callerCts.Token);
        });
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-tg-1", """{"ok":true}""");
        var secretVault = new RecordingSecretVault();
        var service = CreateService(handler, secretVault: secretVault);

        var result = await service.ProvisionAsync(BuildRequest(), callerCts.Token);

        result.Succeeded.Should().BeFalse();
        handler.Requests[^1].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[^1].CancellationToken.CanBeCanceled.Should().BeTrue();
        handler.Requests[^1].CancellationToken.Should().NotBe(callerCts.Token);
        secretVault.RevokeCancellationTokens.Should().ContainSingle();
        secretVault.RevokeCancellationTokens[0].CanBeCanceled.Should().BeTrue();
        secretVault.RevokeCancellationTokens[0].Should().NotBe(callerCts.Token);
    }

    [Fact]
    public async Task ProvisionAsync_rejects_dedicated_internal_transport_without_public_api_base_url()
    {
        var handler = new RecordingHandler();
        var options = new NyxIdToolOptions
        {
            BaseUrl = "http://nyxid.internal:3001",
            InternalApiBaseUrl = "http://nyxid.internal:3001",
        };
        var service = CreateService(handler, options);

        var result = await service.ProvisionAsync(
            new NyxTelegramProvisioningRequest(
                AccessToken: "user-token",
                BotToken: "bot-token",
                WebhookBaseUrl: "https://aevatar.example.com",
                ScopeId: "scope-1",
                Label: "Ops Bot",
                NyxProviderSlug: "api-telegram-bot"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("nyx_api_base_url_not_configured");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task INyxChannelBotProvisioningService_reads_bot_token_from_credentials_map()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", AgentKeyResponse("key-tg-2", "full-key-2"));
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
        result.WorkflowResultDeliveryEnabled.Should().BeTrue();
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

    [Fact]
    public async Task INyxChannelBotProvisioningService_dispatches_runtime_config_to_local_mirror()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", AgentKeyResponse("key-tg-3", "full-key-3"));
        handler.Enqueue("/api/v1/channel-bots", """{"id":"bot-tg-3"}""");
        handler.Enqueue("/api/v1/channel-conversations", """{"id":"route-tg-3"}""");
        EventEnvelope? capturedEnvelope = null;
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);
        var commandFacade = ChannelRegistrationCommandFacadeTestSupport.CreateFacade(
            actorRuntime,
            (IActorDispatchPort)actorRuntime);
        var runtimeConfig = new ChannelBotRuntimeConfig
        {
            Instructions = "Book dinner only after explicit confirmation.",
            DefaultSkill = new ChannelBotRuntimeDefaultSkillConfig
            {
                Name = "booking-capacity",
                Version = "1.0.0",
            },
            CredentialSourceMode = ChannelBotRuntimeCredentialSourceMode.RegistrationAgentKey,
        };
        runtimeConfig.ToolSetRefs.Add("channel.reply.default");
        runtimeConfig.NyxidServiceSelectors.Add(new ChannelBotRuntimeNyxIdServiceSelector
        {
            ServiceSlug = "api-google-workspace",
            EndpointNames = { "calendar_create_event" },
        });
        INyxChannelBotProvisioningService service = CreateService(handler, commandFacade: commandFacade);

        var result = await service.ProvisionAsync(
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
                },
                RuntimeConfig: runtimeConfig),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        capturedEnvelope.Should().NotBeNull();
        var command = capturedEnvelope!.Payload.Unpack<ChannelBotRegisterCommand>();
        command.RuntimeConfig.Should().NotBeNull();
        command.RuntimeConfig.Should().NotBeSameAs(runtimeConfig);
        command.RuntimeConfig.Instructions.Should().Be("Book dinner only after explicit confirmation.");
        command.RuntimeConfig.DefaultSkill.Name.Should().Be("booking-capacity");
        command.RuntimeConfig.ToolSetRefs.Should().BeEquivalentTo("channel.reply.default");
        command.RuntimeConfig.NyxidServiceSelectors.Single().ServiceSlug.Should().Be("api-google-workspace");
        command.RuntimeConfig.NyxidServiceSelectors.Single().EndpointNames.Should().BeEquivalentTo("calendar_create_event");
        command.RuntimeConfig.CredentialSourceMode.Should().Be(ChannelBotRuntimeCredentialSourceMode.RegistrationAgentKey);
    }

    [Fact]
    public async Task ExplicitSelection_NeverFallsThroughToDefaultKeyProvisioning()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys", AgentKeyResponse("key-123", "full-key"));
        handler.Enqueue("/api/v1/channel-bots", """{"id":"bot-456"}""");
        handler.Enqueue("/api/v1/channel-conversations", """{"id":"route-789"}""");
        using var input = System.Text.Json.JsonDocument.Parse("""{"service_ids":[]}""");
        ChannelRegistrationServiceIdsJsonParser.TryParse(input.RootElement, out var selection).Should().BeTrue();
        INyxChannelBotProvisioningService service = CreateService(handler);

        var result = await service.ProvisionAsync(new NyxChannelBotProvisioningRequest(
            "telegram", "user-token", "https://aevatar.example.com", "scope-1", "Ops Bot", "api-telegram-bot",
            Credentials: new Dictionary<string, string> { ["bot_token"] = "bot-token" },
            RequestedServiceSelection: selection), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        handler.Requests.Should().NotContain(request => request.Path == "/api/v1/api-keys");
    }

    private static NyxTelegramProvisioningService CreateService(
        RecordingHandler handler,
        NyxIdToolOptions? options = null,
        ISecretVault? secretVault = null,
        ChannelRegistrationCommandFacade? commandFacade = null,
        IChannelRegistrationOwnerResolver? ownerResolver = null)
    {
        options ??= new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" };
        var nyxClient = new NyxIdApiClient(
            options,
            new HttpClient(handler));

        if (commandFacade is null)
        {
            var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
            actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
                .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
            ((IActorDispatchPort)actorRuntime).DispatchAsync(
                    ChannelBotRegistrationGAgent.WellKnownId,
                    Arg.Any<EventEnvelope>(),
                    Arg.Any<CancellationToken>())
                .Returns(ActorDispatchPortTestSupport.AcceptAsync);
            commandFacade = ChannelRegistrationCommandFacadeTestSupport.CreateFacade(
                actorRuntime,
                (IActorDispatchPort)actorRuntime);
        }

        secretVault ??= new InMemorySecretVault();
        return new NyxTelegramProvisioningService(
            nyxClient,
            options,
            commandFacade,
            new ChannelAgentKeyProvisioningService(
                nyxClient,
                secretVault,
                NullLogger<ChannelAgentKeyProvisioningService>.Instance,
                ChannelAgentKeyWriteMode.NyxIdDefault),
            ownerResolver ?? PersonalOwnerResolver("scope-1"),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxTelegramProvisioningService>>());
    }

    private static IChannelRegistrationOwnerResolver PersonalOwnerResolver(string scopeId)
    {
        var resolver = Substitute.For<IChannelRegistrationOwnerResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), scopeId, Arg.Any<CancellationToken>())
            .Returns(new ChannelRegistrationOwnerResolution(
                new VerifiedChannelRegistrationOwner(
                    scopeId,
                    new ChannelRegistrationKeyOwner(
                        ChannelRegistrationKeyOwnerKind.Personal,
                        scopeId),
                    null),
                string.Empty));
        return resolver;
    }

    private static IChannelRegistrationOwnerResolver OrganizationOwnerResolver(
        string actorId,
        string organizationId)
    {
        var resolver = Substitute.For<IChannelRegistrationOwnerResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), organizationId, Arg.Any<CancellationToken>())
            .Returns(new ChannelRegistrationOwnerResolution(
                new VerifiedChannelRegistrationOwner(
                    actorId,
                    new ChannelRegistrationKeyOwner(
                        ChannelRegistrationKeyOwnerKind.Organization,
                        organizationId),
                    organizationId),
                string.Empty));
        return resolver;
    }

    private static NyxTelegramProvisioningRequest BuildRequest() =>
        new(
            AccessToken: "user-token",
            BotToken: "1234567890:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef-hi",
            WebhookBaseUrl: "https://aevatar.example.com",
            ScopeId: "scope-1",
            Label: "Ops Bot",
            NyxProviderSlug: "api-telegram-bot");

    private static string AgentKeyResponse(string id, string fullKey) =>
        $$"""{"id":"{{id}}","full_key":"{{fullKey}}","purpose":"general","scheduled_write_enabled":false,"scopes":"read write proxy","allow_all_services":true,"allow_all_nodes":true,"allowed_service_ids":[],"allowed_node_ids":[]}""";

    private sealed class RecordingSecretVault(List<string>? effects = null) : ISecretVault
    {
        private readonly InMemorySecretVault _inner = new();

        public Exception? PutException { get; init; }
        public List<StoreSecretRequest> PutRequests { get; } = [];
        public List<RevokeSecretRequest> RevokeRequests { get; } = [];
        public List<CancellationToken> RevokeCancellationTokens { get; } = [];

        public async Task<StoreSecretResult> PutAsync(StoreSecretRequest request, CancellationToken ct = default)
        {
            PutRequests.Add(request);
            if (PutException is not null)
                throw PutException;
            return await _inner.PutAsync(request, ct);
        }

        public Task<ResolveSecretResult> ResolveAsync(ResolveSecretRequest request, CancellationToken ct = default) =>
            _inner.ResolveAsync(request, ct);

        public Task<RotateSecretResult> RotateAsync(RotateSecretRequest request, CancellationToken ct = default) =>
            _inner.RotateAsync(request, ct);

        public Task<RevokeSecretResult> RevokeAsync(RevokeSecretRequest request, CancellationToken ct = default)
        {
            effects?.Add("vault:revoke");
            RevokeRequests.Add(request);
            RevokeCancellationTokens.Add(ct);
            return _inner.RevokeAsync(request, ct);
        }
    }

    private sealed class RecordingHandler(List<string>? effects = null) : HttpMessageHandler
    {
        private readonly Queue<(HttpMethod? Method, string Path, string Body, Func<Exception>? ExceptionFactory)> _responses = new();

        public List<(HttpMethod Method, string Path, string Body, CancellationToken CancellationToken)> Requests { get; } = [];

        public void Enqueue(string path, string body) => _responses.Enqueue((null, path, body, null));

        public void Enqueue(HttpMethod method, string path, string body) =>
            _responses.Enqueue((method, path, body, null));

        public void Enqueue(HttpMethod method, string path, Func<Exception> exceptionFactory) =>
            _responses.Enqueue((method, path, string.Empty, exceptionFactory));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
                throw new InvalidOperationException("No more queued responses.");

            cancellationToken.ThrowIfCancellationRequested();
            var (expectedMethod, expectedPath, responseBody, exceptionFactory) = _responses.Dequeue();
            request.RequestUri.Should().NotBeNull();
            request.RequestUri!.AbsolutePath.Should().Be(expectedPath);
            if (expectedMethod is not null)
                request.Method.Should().Be(expectedMethod);

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, expectedPath, body, cancellationToken));
            if (request.Method == HttpMethod.Delete)
            {
                effects?.Add(expectedPath switch
                {
                    var path when path.StartsWith("/api/v1/channel-conversations/", StringComparison.Ordinal) => "nyx:route-delete",
                    var path when path.StartsWith("/api/v1/channel-bots/", StringComparison.Ordinal) => "nyx:bot-delete",
                    var path when path.StartsWith("/api/v1/api-keys/", StringComparison.Ordinal) => "nyx:key-delete",
                    _ => "nyx:delete",
                });
            }

            if (exceptionFactory is not null)
                throw exceptionFactory();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
