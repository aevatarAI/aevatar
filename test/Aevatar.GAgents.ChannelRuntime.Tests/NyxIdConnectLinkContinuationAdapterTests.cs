using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.NyxidChat.ExternalCallbacks;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdConnectLinkContinuationAdapterTests
{
    [Fact]
    public async Task Create_AcceptsOperationWithoutWaitingForAnotherActor_AndPreservesExactOriginalContext()
    {
        var commands = new Commands();
        var adapter = new NyxIdConnectLinkContinuationAdapter(commands,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-09-30T00:00:00Z")));

        var result = await adapter.CreateAsync(Context(), new ChannelConnectLinkCreateRequest("google-workspace", "Personal Google", "assistant", 900));

        result.Accepted.Should().BeTrue();
        result.CallbackId.Should().Be(commands.Registration!.CallbackId);
        commands.Registration.ConnectLinkRequest.Label.Should().Be("Personal Google");
        commands.Registration.ConnectLinkRequest.RequestedBy.Should().Be("assistant");
        commands.Registration.ConnectLinkRequest.ExpiresInSeconds.Should().Be(900);
        commands.Registration.Should().NotBeNull();
        commands.Registration!.CallbackId.Should().NotBeNullOrWhiteSpace();
        commands.Registration.Kind.Should().Be(ExternalCallbackKind.ConnectLink);
        commands.Registration.Authorization.BindingId.Should().Be("binding-original");
        commands.Registration.Authorization.OwnerScopeId.Should().Be("user-original");
        commands.Registration.Origin.ChannelRegistrationId.Should().Be("registration-original");
        commands.Registration.Origin.ConversationActorId.Should().Be("conversation-original");
        commands.Registration.Origin.OriginalActivity.Conversation.CanonicalKey.Should().Be("telegram:tenant:chat:thread:sender");
        commands.Registration.Origin.OriginalActivity.Conversation.Partition.Should().Be("tenant");
        commands.Registration.Origin.OriginalActivity.Conversation.Scope.Should().Be(ConversationScope.Thread);
        commands.Registration.Origin.OriginalActivity.Conversation.Channel.Value.Should().Be("nyxid");
        commands.Registration.Origin.OriginalActivity.Conversation.Bot.Value.Should().Be("bot-original");
        commands.Registration.Origin.OriginalActivity.From.CanonicalId.Should().Be("routing-participant-original");
        commands.Registration.Authorization.ExternalSubject.ExternalUserId.Should().Be("sender");
        commands.Registration.Origin.OriginalActivity.Content.Text.Should().Be("Connect Google and continue my original action");
        commands.Registration.Origin.OriginalActivity.OutboundDelivery.ReplyMessageId.Should().Be("reply-original");
        commands.Registration.Origin.OriginalActivity.OutboundDelivery.CorrelationId.Should().Be("correlation-original");
        commands.Registration.Origin.OriginalActivity.TransportExtras.DeliveryAddressId.Should().Be("chat-original");
        commands.Registration.Origin.OriginalActivity.TransportExtras.DeliveryAddressType.Should().Be("chat_id");
        commands.Registration.Origin.OriginalActivity.TransportExtras.DeliveryFallbackAddressId.Should().Be("fallback-original");
        commands.Registration.Origin.OriginalActivity.TransportExtras.DeliveryFallbackAddressType.Should().Be("union_id");
        commands.Registration.Origin.OriginalActivity.TransportExtras.NyxAgentApiKeyId.Should().Be("agent-key-original");
        commands.Registration.Origin.OriginalActivity.TransportExtras.NyxConversationId.Should().Be("nyx-conversation-original");
        commands.Registration.Origin.OriginalActivity.TransportExtras.NyxProviderSlug.Should().Be("telegram-bot-original");
        commands.Registration.Origin.OriginalActivity.TransportExtras.NyxLarkUnionId.Should().Be("union-original");
        commands.Registration.Origin.OriginalActivity.TransportExtras.NyxLarkChatId.Should().Be("lark-chat-original");
    }

    [Fact]
    public async Task MissingOriginalSenderAuthorization_FailsClosedEvenWhenOwnerTokenExists()
    {
        var commands = new Commands();
        var context = Context();
        context.Channel.Continuation!.OriginalSenderAuthorization = null;
        var adapter = new NyxIdConnectLinkContinuationAdapter(commands, TimeProvider.System);

        var result = await adapter.CreateAsync(context, new ChannelConnectLinkCreateRequest("google-workspace"));

        result.ErrorCode.Should().Be("original_sender_authorization_missing");
        commands.Registration.Should().BeNull();
    }

    [Fact]
    public async Task MismatchedSavedAuthorization_RejectsAdmissionWithoutOwnerFallback()
    {
        var commands = new Commands();
        var context = Context();
        context.Channel.Continuation!.OriginalSenderAuthorization.BindingId = "wrong-binding";
        var result = await new NyxIdConnectLinkContinuationAdapter(commands, TimeProvider.System)
            .CreateAsync(context, new ChannelConnectLinkCreateRequest("google-workspace"));

        result.ErrorCode.Should().Be("original_sender_authorization_mismatch");
        commands.Registration.Should().BeNull();
    }

    private static AgentToolExecutionContext Context() => AgentToolExecutionContext.Empty with
    {
        Credentials = new AgentToolCredentials("wrong-owner-token", null, null),
        Request = AgentToolRequestIdentity.Empty with { CallId = "tool-action-original" },
        SenderBinding = new AgentToolSenderBindingContext("binding-original", "user-original", "tenant"),
        Caller = AgentToolCallerContext.Empty with { OwnerScopeId = "user-original" },
        NyxIdAuthority = new AgentToolNyxIdAuthorityContext("telegram", "tenant", "sender"),
        Channel = AgentToolChannelContext.Empty with
        {
            BotRegistrationId = "registration-original", Platform = "telegram",
            Continuation = new AgentToolChannelContinuationContext
            {
                ConversationActorId = "conversation-original", ChannelRegistrationId = "registration-original",
                CanonicalConversationKey = "telegram:tenant:chat:thread:sender", Platform = "telegram", TenantId = "tenant",
                ChatId = "chat", SenderId = "routing-participant-original", ThreadId = "thread", OriginalActivityId = "activity-original",
                OriginalUserText = "Connect Google and continue my original action", ChannelId = "nyxid", BotId = "bot-original",
                ConversationScope = AgentToolChannelConversationScope.Thread, ConversationPartition = "tenant",
                NyxAgentApiKeyId = "agent-key-original", NyxConversationId = "nyx-conversation-original", NyxProviderSlug = "telegram-bot-original",
                DeliveryAddressId = "chat-original", DeliveryAddressType = "chat_id",
                DeliveryFallbackAddressId = "fallback-original", DeliveryFallbackAddressType = "union_id",
                NyxLarkUnionId = "union-original", NyxLarkChatId = "lark-chat-original",
                ReplyMessageId = "reply-original", OutboundCorrelationId = "correlation-original",
                OriginalSenderAuthorization = new AgentToolChannelSenderAuthorization
                {
                    OwnerScopeId = "user-original", NyxUserId = "user-original",
                    Platform = "telegram", Tenant = "tenant", ExternalUserId = "sender", BindingId = "binding-original",
                },
            },
        },
    };

    private sealed class Commands : IExternalCallbackCommandPort
    {
        public ExternalCallbackRegistration? Registration { get; private set; }
        public Task<ExternalCallbackRegistration> AdmitAsync(ExternalCallbackRegistration registration, CancellationToken ct = default)
        {
            Registration = registration.Clone();
            Registration.OperationActorId = "operation-created";
            return Task.FromResult(Registration.Clone());
        }
        public Task HintAsync(string? callbackId, string externalRequestId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SubmitOAuthAsync(OAuthContinuationSubmission submission, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
