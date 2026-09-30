using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.ChannelRuntime.Tests.Identity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed partial class ChannelConversationTurnRunnerTests
{
    [Theory]
    [InlineData("link", "")]
    [InlineData("resume", "")]
    [InlineData("reply", "")]
    [InlineData("link", "key")]
    [InlineData("resume", "key")]
    [InlineData("reply", "key")]
    [InlineData("link", "scope")]
    [InlineData("resume", "scope")]
    [InlineData("reply", "scope")]
    [InlineData("link", "bot")]
    [InlineData("resume", "bot")]
    [InlineData("reply", "bot")]
    [InlineData("link", "missing-transport-key")]
    [InlineData("resume", "missing-transport-key")]
    [InlineData("reply", "missing-transport-key")]
    public async Task ExternalCallback_ShouldValidateTelegramRelayAgentKeyIdentity(string stage, string mismatch)
    {
        var registration = BuildNewRegistrationEntry();
        registration.Platform = "telegram";
        registration.NyxAgentApiKeyId.Should().NotBe(registration.Id);
        var vault = new InMemorySecretVault();
        var stored = await vault.PutAsync(new StoreSecretRequest(CredentialSecretPurposes.ChannelNyxIdAgentKey,
            registration.ScopeId, registration.NyxAgentApiKeyId, "original-registration-key", "callback-test"));
        registration.ChannelAgentKey.SecretReference = stored.Reference.Clone();
        registration.WorkflowResultDeliveryCredential = stored.Reference.Clone();
        using var services = new ServiceCollection().AddSingleton<ISecretVault>(vault).BuildServiceProvider();
        var relay = new RecordingJsonHandler("""{"message_id":"continued-message"}""");
        var runner = CreateRunner(BuildRegistrationQueryPort(registration), new RecordingPlatformAdapter(), services, relayHandler: relay);
        var parsed = new NyxIdRelayTransport().Parse(Encoding.UTF8.GetBytes($$$"""
            {"message_id":"original-relay-message","platform":"telegram",
             "agent":{"api_key_id":"{{{registration.NyxAgentApiKeyId}}}"},
             "conversation":{"type":"private","id":"original-chat"},
             "sender":{"platform_id":"sender-original"},"content":{"type":"text","text":"/init"}}
            """));
        parsed.Success.Should().BeTrue();
        var activity = parsed.Activity!;
        activity.Bot.Value.Should().Be(registration.NyxAgentApiKeyId);
        activity.TransportExtras.NyxRegistrationScopeId = registration.ScopeId;
        switch (mismatch)
        {
            case "key": activity.TransportExtras.NyxAgentApiKeyId = "other-agent-key"; break;
            case "scope": activity.TransportExtras.NyxRegistrationScopeId = "other-scope"; break;
            case "bot": activity.Bot = BotInstanceId.From("other-bot"); break;
            case "missing-transport-key": activity.TransportExtras.NyxAgentApiKeyId = string.Empty; break;
        }
        var origin = new ChannelCallbackOrigin
        {
            ConversationActorId = "original-conversation", ChannelRegistrationId = registration.Id,
            ActionId = activity.Id, OriginalActivity = activity,
        };
        var context = ConversationTurnRuntimeContext.Empty with
        {
            ConversationActorId = origin.ConversationActorId, UseRegistrationOutbound = true,
        };

        var result = stage switch
        {
            "link" => await runner.RunExternalCallbackLinkAsync(new ExternalCallbackLinkReady
            {
                CallbackId = "callback-relay", Origin = origin, Kind = ExternalCallbackKind.Oauth,
                ConnectUrl = "https://id.example.test/connect",
            }, context, CancellationToken.None),
            "resume" => await runner.RunExternalCallbackAsync(activity, origin, new CallbackAuthorizationReference(),
                new ExternalCallbackVerifiedReferences(), CallbackResult.Failed, context, CancellationToken.None),
            _ => await runner.RunLlmReplyAsync(new LlmReplyReadyEvent
            {
                RegistrationId = registration.Id, Activity = activity, CorrelationId = "callback-relay",
                Outbound = new MessageContent { Text = "Continuing the original request." },
            }, context, CancellationToken.None),
        };

        if (mismatch.Length > 0)
        {
            result.Success.Should().BeFalse();
            result.FailureKind.Should().Be(FailureKind.PermanentAdapterError);
            result.LlmReplyRequest.Should().BeNull();
            relay.Requests.Should().BeEmpty();
            return;
        }

        result.Success.Should().BeTrue("a Relay Agent Key ID is a transport identity, not a registration ID");
        if (stage == "resume")
            result.LlmReplyRequest.Should().NotBeNull();
        else
        {
            relay.Requests.Should().ContainSingle().Which.Authorization.Should().Contain("original-registration-key");
            relay.Requests[0].Body.Should().Contain("original-relay-message");
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tombstoned")]
    [InlineData("saved-key")]
    [InlineData("saved-scope")]
    [InlineData("saved-registration")]
    public async Task ExternalCallbackResume_ShouldRejectUnavailableOriginalRegistration(string reason)
    {
        var registration = BuildNewRegistrationEntry();
        registration.Tombstoned = reason == "tombstoned";
        var query = reason == "missing"
            ? Substitute.For<IChannelBotRegistrationQueryPort>()
            : BuildRegistrationQueryPort(registration);
        var runner = CreateRunner(query, new RecordingPlatformAdapter());
        var original = BuildInboundActivity("continue", "original-activity", ConversationScope.DirectMessage,
            transportExtras: new TransportExtras
            {
                NyxAgentApiKeyId = reason == "saved-key" ? "different-key" : registration.NyxAgentApiKeyId,
                NyxRegistrationScopeId = reason == "saved-scope" ? "different-scope" : registration.ScopeId,
            }, botId: reason == "saved-registration" ? "other-registration" : registration.Id);
        var origin = new ChannelCallbackOrigin
        {
            ConversationActorId = "original-conversation", ChannelRegistrationId = registration.Id,
            ActionId = original.Id, OriginalActivity = original,
        };

        var result = await runner.RunExternalCallbackAsync(original, origin,
            new CallbackAuthorizationReference(), new ExternalCallbackVerifiedReferences(), CallbackResult.Failed,
            ConversationTurnRuntimeContext.Empty with { ConversationActorId = "original-conversation" }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(FailureKind.PermanentAdapterError);
        result.ErrorCode.Should().Be("callback_origin_unavailable");
        result.LlmReplyRequest.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalCallbackLink_ShouldRejectMissingOrTombstonedOriginBeforeOutbound(bool missing)
    {
        var registration = BuildRegistrationEntry();
        registration.Tombstoned = true;
        var relay = new RecordingJsonHandler("{\"message_id\":\"unexpected\"}");
        var query = missing ? Substitute.For<IChannelBotRegistrationQueryPort>() : BuildRegistrationQueryPort(registration);
        var runner = CreateRunner(query, new RecordingPlatformAdapter(), relayHandler: relay);
        var activity = BuildInboundActivity("continue", "original-activity", ConversationScope.DirectMessage);

        var result = await runner.RunExternalCallbackLinkAsync(new ExternalCallbackLinkReady
        {
            CallbackId = "callback-tombstoned",
            Kind = ExternalCallbackKind.ConnectLink,
            ConnectUrl = "https://example.test/link",
            Origin = new ChannelCallbackOrigin
            {
                ConversationActorId = "original-conversation",
                ChannelRegistrationId = registration.Id,
                OriginalActivity = activity,
            },
        }, ConversationTurnRuntimeContext.Empty with { ConversationActorId = "original-conversation" }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(FailureKind.PermanentAdapterError);
        result.ErrorCode.Should().Be("callback_origin_unavailable");
        relay.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExternalCallbackLink_ShouldRejectPlatformMismatchBeforeOutbound()
    {
        var registration = BuildRegistrationEntry();
        var relay = new RecordingJsonHandler("{\"message_id\":\"unexpected\"}");
        var runner = CreateRunner(BuildRegistrationQueryPort(registration), new RecordingPlatformAdapter(), relayHandler: relay);
        var activity = BuildInboundActivity("continue", "original-activity", ConversationScope.DirectMessage);
        activity.ChannelId = ChannelId.From("telegram");

        var result = await runner.RunExternalCallbackLinkAsync(new ExternalCallbackLinkReady
        {
            CallbackId = "callback-platform-mismatch",
            Kind = ExternalCallbackKind.ConnectLink,
            ConnectUrl = "https://example.test/link",
            Origin = new ChannelCallbackOrigin
            {
                ConversationActorId = "original-conversation",
                ChannelRegistrationId = registration.Id,
                OriginalActivity = activity,
            },
        }, ConversationTurnRuntimeContext.Empty with { ConversationActorId = "original-conversation" }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(FailureKind.PermanentAdapterError);
        result.ErrorCode.Should().Be("callback_origin_unavailable");
        relay.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExternalCallbackResume_ShouldRejectSavedOriginPlatformMismatch()
    {
        var registration = BuildRegistrationEntry();
        var runner = CreateRunner(BuildRegistrationQueryPort(registration), new RecordingPlatformAdapter());
        var original = BuildInboundActivity("continue", "original-activity", ConversationScope.DirectMessage);
        original.ChannelId = ChannelId.From("telegram");
        var origin = new ChannelCallbackOrigin
        {
            ConversationActorId = "original-conversation", ChannelRegistrationId = registration.Id,
            ActionId = original.Id, OriginalActivity = original,
        };

        var result = await runner.RunExternalCallbackAsync(original, origin,
            new CallbackAuthorizationReference
            {
                ExternalSubject = new ExternalSubjectRef { Platform = "lark", Tenant = "sender-tenant", ExternalUserId = "sender" },
            }, new ExternalCallbackVerifiedReferences(), CallbackResult.Failed,
            ConversationTurnRuntimeContext.Empty with { ConversationActorId = "original-conversation" }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(FailureKind.PermanentAdapterError);
        result.ErrorCode.Should().Be("callback_origin_unavailable");
    }

    [Fact]
    public async Task OriginalInbound_ShouldCarryTrustedFullConversationAndSenderAuthorityIntoToolContext()
    {
        var broker = new InMemoryCapabilityBroker();
        broker.SeedBinding(new ExternalSubjectRef { Platform = "lark", Tenant = "scope-1", ExternalUserId = "ou_user_1" },
            new BindingId { Value = "original-binding" });
        var services = new ServiceCollection()
            .AddSingleton<IExternalIdentityBindingQueryPort>(broker)
            .AddSingleton<INyxIdCapabilityBroker>(broker)
            .AddSingleton<IOwnerScopeResolver>(new StubOwnerScopeResolver("original-user-owner"))
            .BuildServiceProvider();
        var runner = CreateRunner(BuildRegistrationQueryPort(), new RecordingPlatformAdapter(), services);
        var activity = BuildInboundActivity("Connect my original Google workspace", "source-operation", ConversationScope.DirectMessage,
            "original-chat", transportExtras: new TransportExtras
            {
                NyxPlatform = "lark", NyxConversationId = "original-chat", NyxLarkUnionId = "original-union",
                DeliveryAddressId = "original-destination", DeliveryAddressType = "chat_id", NyxUserAccessToken = "transient-token",
            });
        activity.OutboundDelivery = new OutboundDeliveryContext { ReplyMessageId = "original-anchor", CorrelationId = activity.Id };

        var result = await runner.RunInboundAsync(activity,
            ConversationTurnRuntimeContext.Empty with { ConversationActorId = "trusted-conversation-actor" }, CancellationToken.None);

        var origin = result.LlmReplyRequest!.ToolContext.Channel.Continuation;
        origin.ConversationActorId.Should().Be("trusted-conversation-actor");
        origin.ChannelRegistrationId.Should().Be("reg-1");
        origin.CanonicalConversationKey.Should().Be(activity.Conversation.CanonicalKey);
        origin.ConversationPartition.Should().Be(activity.Conversation.Partition);
        origin.OriginalUserText.Should().Be("Connect my original Google workspace");
        origin.OriginalSenderAuthorization.BindingId.Should().Be("original-binding");
        origin.OriginalSenderAuthorization.OwnerScopeId.Should().Be("original-user-owner");
        origin.OriginalSenderAuthorization.ExternalUserId.Should().Be("ou_user_1");
        origin.NyxLarkUnionId.Should().Be("original-union");
        origin.DeliveryAddressId.Should().Be("original-destination");
        origin.ReplyMessageId.Should().Be("original-anchor");
        origin.OutboundCorrelationId.Should().Be(activity.OutboundDelivery.CorrelationId);
        origin.ToString().Should().NotContain("transient-token");
    }

    [Theory]
    [InlineData(CallbackResult.Cancelled)]
    [InlineData(CallbackResult.Failed)]
    [InlineData(CallbackResult.Expired)]
    public async Task UnboundExternalCallbackFailure_ShouldResumeWithNoBusinessToolAuthority(CallbackResult outcome)
    {
        var registration = BuildRegistrationEntry();
        var runner = CreateRunner(BuildRegistrationQueryPort(registration), new RecordingPlatformAdapter());
        var activity = BuildInboundActivity("Continue after authorization failed", "original-init", ConversationScope.DirectMessage);
        var result = await runner.RunExternalCallbackAsync(activity,
            new ChannelCallbackOrigin { ConversationActorId = "original-conversation", ChannelRegistrationId = registration.Id,
                ActionId = activity.Id, OriginalActivity = activity.Clone() },
            new CallbackAuthorizationReference
            {
                ExternalSubject = new ExternalSubjectRef { Platform = "lark", Tenant = "sender-tenant", ExternalUserId = "sender-original" },
            }, new ExternalCallbackVerifiedReferences(), outcome,
            ConversationTurnRuntimeContext.Empty with { ConversationActorId = "original-conversation" }, CancellationToken.None);

        var toolContext = Aevatar.AI.Abstractions.ToolProviders.AgentToolExecutionContextMapper.FromPayload(result.LlmReplyRequest!.ToolContext);
        toolContext.ToolVisibility.IsRestricted.Should().BeTrue();
        toolContext.ToolVisibility.AllowedToolNames.Should().BeEmpty();
        toolContext.DurableNyxIdCredential.Should().BeNull();
        toolContext.SenderBinding.BindingId.Should().BeNull();
    }

    [Fact]
    public async Task ExternalCallbackResume_ShouldBuildNormalLlmRunForOriginalInitWithoutStartingAnotherOAuth()
    {
        var registration = BuildRegistrationEntry();
        var runner = CreateRunner(BuildRegistrationQueryPort(registration), new RecordingPlatformAdapter());
        var original = BuildInboundActivity("/init", "original-init", ConversationScope.DirectMessage);
        var origin = new ChannelCallbackOrigin
        {
            ConversationActorId = "conversation-original", ChannelRegistrationId = registration.Id,
            ActionId = original.Id, OriginalActivity = original,
        };
        var resumed = original.Clone();
        resumed.Content.Text = "/init\nExternal authorization succeeded. Resume the original conversation.";

        var result = await runner.RunExternalCallbackAsync(resumed, origin,
            new CallbackAuthorizationReference
            {
                ExternalSubject = new ExternalSubjectRef { Platform = "lark", Tenant = "original-tenant", ExternalUserId = "original-subject" },
                BindingId = "binding-new", OwnerScopeId = "sender-owner",
            },
            new ExternalCallbackVerifiedReferences { BindingId = "binding-new", OwnerScopeId = "sender-owner" },
            CallbackResult.Succeeded,
            ConversationTurnRuntimeContext.Empty with { ConversationActorId = "conversation-original", UseRegistrationOutbound = true },
            CancellationToken.None);

        result.LlmReplyRequest.Should().NotBeNull();
        var request = result.LlmReplyRequest!;
        request.Activity.Conversation.Should().BeEquivalentTo(original.Conversation);
        request.RegistrationId.Should().Be(registration.Id);
        request.ToolContext.SenderBinding.BindingId.Should().Be("binding-new");
        request.ToolContext.NyxIdAuthority.ExternalUserId.Should().Be("original-subject");
        request.ToolContext.Channel.Continuation.ConversationActorId.Should().Be("conversation-original");
        request.ToolContext.Channel.Continuation.OriginalSenderAuthorization.OwnerScopeId.Should().Be("sender-owner");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalCallbackReply_ShouldUseExactRegistrationAgentKeyAfterOriginalReplyTokenExpires(bool legacyRegistration)
    {
        var registration = legacyRegistration ? BuildRegistrationEntry() : BuildNewRegistrationEntry();
        if (legacyRegistration)
            registration.NyxAgentApiKeyId = "legacy-original-key";
        var vault = new InMemorySecretVault();
        var stored = await vault.PutAsync(new StoreSecretRequest(
            legacyRegistration ? CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey : CredentialSecretPurposes.ChannelNyxIdAgentKey,
            registration.ScopeId, registration.NyxAgentApiKeyId,
            "original-registration-key", "callback-test"));
        if (!legacyRegistration)
            registration.ChannelAgentKey.SecretReference = stored.Reference.Clone();
        registration.WorkflowResultDeliveryCredential = stored.Reference.Clone();
        var services = new ServiceCollection().AddSingleton<ISecretVault>(vault).BuildServiceProvider();
        var relay = new RecordingJsonHandler("""{"message_id":"continued-message"}""");
        var runner = CreateRunner(BuildRegistrationQueryPort(registration), new RecordingPlatformAdapter(), services, relayHandler: relay);
        var activity = BuildInboundActivity("original action", "original-activity", ConversationScope.Thread, "thread-original",
            transportExtras: new TransportExtras { NyxPlatform = "lark", NyxAgentApiKeyId = registration.NyxAgentApiKeyId });
        activity.OutboundDelivery = new OutboundDeliveryContext { ReplyMessageId = "original-relay-anchor", CorrelationId = "callback-turn" };

        var result = await runner.RunLlmReplyAsync(new LlmReplyReadyEvent
        {
            RegistrationId = registration.Id, Activity = activity, CorrelationId = "callback-turn",
            Outbound = new MessageContent { Text = "The original action continued." },
        }, ConversationTurnRuntimeContext.Empty with { UseRegistrationOutbound = true }, CancellationToken.None);

        result.Success.Should().BeTrue();
        relay.Requests.Should().ContainSingle();
        relay.Requests[0].Authorization.Should().Contain("original-registration-key");
        relay.Requests[0].Body.Should().Contain("original-relay-anchor");
        relay.Requests[0].Body.Should().Contain("The original action continued.");
    }

    [Fact]
    public async Task ExternalCallbackReply_ShouldRejectDifferentRegistrationKeyBeforeOutbound()
    {
        var registration = BuildNewRegistrationEntry();
        var relay = new RecordingJsonHandler("""{"message_id":"unexpected"}""");
        var runner = CreateRunner(BuildRegistrationQueryPort(registration), new RecordingPlatformAdapter(), relayHandler: relay);
        var activity = BuildInboundActivity("original action", "original-activity", ConversationScope.DirectMessage,
            transportExtras: new TransportExtras { NyxAgentApiKeyId = "another-registration-key" });
        activity.OutboundDelivery = new OutboundDeliveryContext { ReplyMessageId = "original-anchor", CorrelationId = "callback-turn" };

        var result = await runner.RunLlmReplyAsync(new LlmReplyReadyEvent
        {
            RegistrationId = registration.Id, Activity = activity, CorrelationId = "callback-turn", Outbound = new MessageContent { Text = "continued" },
        }, ConversationTurnRuntimeContext.Empty with { UseRegistrationOutbound = true }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("callback_registration_mismatch");
        relay.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tombstoned")]
    [InlineData("platform")]
    [InlineData("scope")]
    public async Task ExternalCallbackReply_ShouldRejectUnavailableOriginalRegistrationBeforeOutbound(string reason)
    {
        var registration = BuildNewRegistrationEntry();
        registration.Tombstoned = reason == "tombstoned";
        var query = reason == "missing"
            ? Substitute.For<IChannelBotRegistrationQueryPort>()
            : BuildRegistrationQueryPort(registration);
        var relay = new RecordingJsonHandler("{\"message_id\":\"unexpected\"}");
        var runner = CreateRunner(query, new RecordingPlatformAdapter(), relayHandler: relay);
        var activity = BuildInboundActivity("original action", "original-activity", ConversationScope.DirectMessage,
            transportExtras: new TransportExtras
            {
                NyxPlatform = reason == "platform" ? "telegram" : "lark",
                NyxAgentApiKeyId = registration.NyxAgentApiKeyId,
                NyxRegistrationScopeId = reason == "scope" ? "different-scope" : registration.ScopeId,
            });
        activity.OutboundDelivery = new OutboundDeliveryContext { ReplyMessageId = "original-anchor", CorrelationId = "callback-turn" };

        var result = await runner.RunLlmReplyAsync(new LlmReplyReadyEvent
        {
            RegistrationId = registration.Id, Activity = activity, CorrelationId = "callback-turn", Outbound = new MessageContent { Text = "continued" },
        }, ConversationTurnRuntimeContext.Empty with { UseRegistrationOutbound = true }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(FailureKind.PermanentAdapterError);
        result.ErrorCode.Should().Be(reason == "scope" ? "callback_registration_mismatch" : "callback_origin_unavailable");
        relay.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExternalCallbackReply_ShouldRetryTransientOriginalRegistrationLookupFailure()
    {
        var query = Substitute.For<IChannelBotRegistrationQueryPort>();
        query.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ChannelBotRegistrationEntry?>(new InvalidOperationException("temporary query outage")));
        var relay = new RecordingJsonHandler("{\"message_id\":\"unexpected\"}");
        var runner = CreateRunner(query, new RecordingPlatformAdapter(), relayHandler: relay);
        var activity = BuildInboundActivity("original action", "original-activity", ConversationScope.DirectMessage);
        activity.OutboundDelivery = new OutboundDeliveryContext { ReplyMessageId = "original-anchor", CorrelationId = "callback-turn" };

        var result = await runner.RunLlmReplyAsync(new LlmReplyReadyEvent
        {
            RegistrationId = "reg-1", Activity = activity, CorrelationId = "callback-turn", Outbound = new MessageContent { Text = "continued" },
        }, ConversationTurnRuntimeContext.Empty with { UseRegistrationOutbound = true }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(FailureKind.TransientAdapterError);
        result.ErrorCode.Should().Be("callback_registration_unavailable");
        relay.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExternalCallbackReply_ShouldClassifyMissingDeliveryCredentialAsPermanent()
    {
        var registration = BuildRegistrationEntry();
        var relay = new RecordingJsonHandler("{\"message_id\":\"unexpected\"}");
        var runner = CreateRunner(BuildRegistrationQueryPort(registration), new RecordingPlatformAdapter(), relayHandler: relay);
        var activity = BuildInboundActivity("original action", "original-activity", ConversationScope.DirectMessage,
            transportExtras: new TransportExtras { NyxPlatform = "lark" });
        activity.OutboundDelivery = new OutboundDeliveryContext { ReplyMessageId = "original-anchor", CorrelationId = "callback-turn" };

        var result = await runner.RunLlmReplyAsync(new LlmReplyReadyEvent
        {
            RegistrationId = registration.Id, Activity = activity, CorrelationId = "callback-turn", Outbound = new MessageContent { Text = "continued" },
        }, ConversationTurnRuntimeContext.Empty with { UseRegistrationOutbound = true }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(FailureKind.PermanentAdapterError);
        result.ErrorCode.Should().Be("callback_delivery_credential_missing");
        relay.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(SecretResolutionFailureReason.NotFound, FailureKind.TransientAdapterError)]
    [InlineData(SecretResolutionFailureReason.Revoked, FailureKind.PermanentAdapterError)]
    [InlineData(SecretResolutionFailureReason.Unauthorized, FailureKind.PermanentAdapterError)]
    public async Task ExternalCallbackReply_ShouldDistinguishTemporarySecretAbsenceFromPermanentCredentialFailure(
        SecretResolutionFailureReason reason, FailureKind expected)
    {
        var registration = BuildNewRegistrationEntry();
        var vault = Substitute.For<ISecretVault>();
        vault.ResolveAsync(Arg.Any<ResolveSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ResolveSecretResult(null, null, reason));
        var services = new ServiceCollection().AddSingleton(vault).BuildServiceProvider();
        var relay = new RecordingJsonHandler("{\"message_id\":\"unexpected\"}");
        var runner = CreateRunner(BuildRegistrationQueryPort(registration), new RecordingPlatformAdapter(), services, relayHandler: relay);
        var activity = BuildInboundActivity("original action", "original-activity", ConversationScope.DirectMessage,
            transportExtras: new TransportExtras { NyxPlatform = "lark", NyxAgentApiKeyId = registration.NyxAgentApiKeyId });
        activity.OutboundDelivery = new OutboundDeliveryContext { ReplyMessageId = "original-anchor", CorrelationId = "callback-turn" };

        var result = await runner.RunLlmReplyAsync(new LlmReplyReadyEvent
        {
            RegistrationId = registration.Id, Activity = activity, CorrelationId = "callback-turn", Outbound = new MessageContent { Text = "continued" },
        }, ConversationTurnRuntimeContext.Empty with { UseRegistrationOutbound = true }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(expected);
        relay.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalCallbackReply_ShouldRecoverAfterTransientVaultResolutionFailure(bool exception)
    {
        var registration = BuildNewRegistrationEntry();
        var vault = new FlakyExternalCallbackVault(exception);
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ChannelNyxIdAgentKey, registration.ScopeId, registration.NyxAgentApiKeyId,
            "original-registration-key", "callback-test"));
        registration.ChannelAgentKey.SecretReference = stored.Reference.Clone();
        registration.WorkflowResultDeliveryCredential = stored.Reference.Clone();
        var services = new ServiceCollection().AddSingleton<ISecretVault>(vault).BuildServiceProvider();
        var relay = new RecordingJsonHandler("{\"message_id\":\"continued-message\"}");
        var runner = CreateRunner(BuildRegistrationQueryPort(registration), new RecordingPlatformAdapter(), services, relayHandler: relay);
        var activity = BuildInboundActivity("original action", "original-activity", ConversationScope.DirectMessage,
            transportExtras: new TransportExtras { NyxPlatform = "lark", NyxAgentApiKeyId = registration.NyxAgentApiKeyId });
        activity.OutboundDelivery = new OutboundDeliveryContext { ReplyMessageId = "original-anchor", CorrelationId = "callback-turn" };
        var reply = new LlmReplyReadyEvent
        {
            RegistrationId = registration.Id, Activity = activity, CorrelationId = "callback-turn",
            Outbound = new MessageContent { Text = "continued" },
        };

        var first = await runner.RunLlmReplyAsync(reply, ConversationTurnRuntimeContext.Empty with { UseRegistrationOutbound = true }, CancellationToken.None);
        first.Success.Should().BeFalse();
        first.FailureKind.Should().Be(FailureKind.TransientAdapterError);
        first.ErrorCode.Should().Be("callback_agent_key_unavailable");

        var second = await runner.RunLlmReplyAsync(reply, ConversationTurnRuntimeContext.Empty with { UseRegistrationOutbound = true }, CancellationToken.None);
        second.Success.Should().BeTrue();
        relay.Requests.Should().ContainSingle();
    }

    private sealed class FlakyExternalCallbackVault(bool exception) : ISecretVault
    {
        private readonly InMemorySecretVault _inner = new();
        private bool _failNext = true;

        public Task<StoreSecretResult> PutAsync(StoreSecretRequest request, CancellationToken ct = default) => _inner.PutAsync(request, ct);

        public Task<ResolveSecretResult> ResolveAsync(ResolveSecretRequest request, CancellationToken ct = default)
        {
            if (_failNext)
            {
                _failNext = false;
                return exception
                    ? Task.FromException<ResolveSecretResult>(new InvalidOperationException("temporary vault outage"))
                    : Task.FromResult(new ResolveSecretResult(null, null, SecretResolutionFailureReason.NotFound));
            }

            return _inner.ResolveAsync(request, ct);
        }

        public Task<RotateSecretResult> RotateAsync(RotateSecretRequest request, CancellationToken ct = default) => _inner.RotateAsync(request, ct);

        public Task<RevokeSecretResult> RevokeAsync(RevokeSecretRequest request, CancellationToken ct = default) => _inner.RevokeAsync(request, ct);
    }
}
