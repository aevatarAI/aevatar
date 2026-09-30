using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public sealed partial class ChannelConversationTurnRunner
{
    public async Task<ConversationTurnResult> RunExternalCallbackLinkAsync(ExternalCallbackLinkReady link,
        ConversationTurnRuntimeContext runtimeContext, CancellationToken ct)
    {
        var registration = await ResolveRegistrationAsync(link.Origin.ChannelRegistrationId, ct);
        if (registration is not { Tombstoned: false } ||
            !IsCallbackOriginRegistrationMatch(link.Origin, registration, runtimeContext.ConversationActorId))
            return ConversationTurnResult.PermanentFailure("callback_origin_unavailable", "The original Channel registration is unavailable.");
        var activity = link.Origin.OriginalActivity.Clone();
        if (activity.OutboundDelivery is not null)
            activity.OutboundDelivery.CorrelationId = link.CallbackId;
        var text = link.Kind == ExternalCallbackKind.Oauth
            ? $"请打开以下链接完成 NyxID 授权，完成后我会在当前会话继续：\n{link.ConnectUrl}"
            : $"请打开以下链接完成服务连接，完成后我会在当前会话继续：\n{link.ConnectUrl}";
        return await SendReplyAsync(new MessageContent { Text = text }, link.CallbackId,
            activity.Conversation, ToInboundMessage(activity), registration,
            runtimeContext with { UseRegistrationOutbound = true }, ct);
    }

    public async Task<ConversationTurnResult> RunExternalCallbackAsync(
        ChatActivity activity,
        ChannelCallbackOrigin origin,
        CallbackAuthorizationReference authorization,
        ExternalCallbackVerifiedReferences references,
        CallbackResult result,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        var snapshot = await ResolveRegistrationSnapshotAsync(origin.ChannelRegistrationId, ct);
        if (snapshot?.Registration is not { Tombstoned: false } registration ||
            !IsCallbackOriginRegistrationMatch(origin, registration, runtimeContext.ConversationActorId) ||
            !IsRegistrationActivityMatch(activity, registration))
            return ConversationTurnResult.PermanentFailure("callback_origin_unavailable", "The original Channel registration is unavailable.");

        // Use the saved authorization subject, never a subject inferred from the latest
        // conversation or from registration ownership. The broker re-mints only this binding.
        var bindingId = result == CallbackResult.Succeeded
            ? references.BindingId : authorization.BindingId;
        var ownerScopeId = result == CallbackResult.Succeeded
            ? references.OwnerScopeId : authorization.OwnerScopeId;
        ResolvedSenderBinding? sender = null;
        if (authorization.ExternalSubject is not null && !string.IsNullOrWhiteSpace(bindingId) &&
            !string.IsNullOrWhiteSpace(ownerScopeId))
        {
            sender = new ResolvedSenderBinding(bindingId, authorization.ExternalSubject.Clone(), ownerScopeId);
        }

        var inbound = ToInboundMessage(activity);
        var inboundEvent = ToInboundEvent(activity, registration, inbound);
        var request = await BuildLlmReplyRequestAsync(activity, registration, inboundEvent, runtimeContext,
            sender, snapshot.StateVersion, ct, allowDefaultSkillRouting: sender is not null, allowSenderCredentialIssuance: false);
        if (sender is null)
        {
            // The registration may supply the LLM provider, but a cancelled or failed
            // first binding must never grant its owner's business tool authority.
            request.ToolContext = (AgentToolExecutionContextMapper.FromPayload(request.ToolContext) with
            {
                ToolVisibility = AgentToolVisibilityScope.Empty,
                SkillRecovery = AgentSkillRecoveryContext.Empty,
                Credentials = AgentToolCredentials.Empty,
                DurableNyxIdCredential = null,
            }).ToPayload();
        }
        return ConversationTurnResult.LlmReplyRequested(request);
    }

    private static ChannelCallbackOrigin? BuildCallbackOrigin(ChatActivity activity,
        ChannelBotRegistrationEntry registration, ConversationTurnRuntimeContext runtimeContext)
    {
        if (string.IsNullOrWhiteSpace(runtimeContext.ConversationActorId) || activity.Conversation is null)
            return null;
        var original = activity.Clone();
        if (original.TransportExtras is not null)
            original.TransportExtras.NyxUserAccessToken = string.Empty;
        return new ChannelCallbackOrigin
        {
            ConversationActorId = runtimeContext.ConversationActorId,
            ChannelRegistrationId = registration.Id,
            ActionId = activity.Id,
            OriginalActivity = original,
        };
    }

    private static AgentToolChannelContinuationContext? BuildToolChannelContinuation(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration,
        ConversationTurnRuntimeContext runtimeContext,
        ResolvedSenderBinding? sender)
    {
        if (string.IsNullOrWhiteSpace(runtimeContext.ConversationActorId) || activity.Conversation is null)
            return null;
        var extras = activity.TransportExtras;
        return new AgentToolChannelContinuationContext
        {
            ConversationActorId = runtimeContext.ConversationActorId,
            ChannelRegistrationId = registration.Id,
            CanonicalConversationKey = activity.Conversation.CanonicalKey,
            Platform = extras?.NyxPlatform ?? activity.ChannelId?.Value ?? string.Empty,
            TenantId = sender?.Subject.Tenant ?? string.Empty,
            ChatId = extras?.NyxConversationId ?? activity.Conversation.Partition,
            SenderId = activity.From?.CanonicalId ?? string.Empty,
            // ConversationReference owns the complete opaque thread route. Do not parse
            // its canonical key to manufacture a platform thread identifier.
            ThreadId = string.Empty,
            OriginalActivityId = activity.Id,
            OriginalUserText = activity.Content?.Text ?? string.Empty,
            ConversationPartition = activity.Conversation.Partition,
            ConversationScope = activity.Conversation.Scope switch
            {
                ConversationScope.DirectMessage => AgentToolChannelConversationScope.DirectMessage,
                ConversationScope.Group => AgentToolChannelConversationScope.Group,
                ConversationScope.Channel => AgentToolChannelConversationScope.Channel,
                ConversationScope.Thread => AgentToolChannelConversationScope.Thread,
                _ => AgentToolChannelConversationScope.Unspecified,
            },
            ChannelId = activity.ChannelId?.Value ?? string.Empty,
            BotId = activity.Bot?.Value ?? registration.Id,
            NyxAgentApiKeyId = extras?.NyxAgentApiKeyId ?? string.Empty,
            NyxConversationId = extras?.NyxConversationId ?? string.Empty,
            NyxProviderSlug = extras?.NyxProviderSlug ?? registration.NyxProviderSlug ?? string.Empty,
            ReplyMessageId = activity.OutboundDelivery?.ReplyMessageId ?? string.Empty,
            OutboundCorrelationId = activity.OutboundDelivery?.CorrelationId ?? string.Empty,
            DeliveryAddressId = extras?.DeliveryAddressId ?? string.Empty,
            DeliveryAddressType = extras?.DeliveryAddressType ?? string.Empty,
            DeliveryFallbackAddressId = extras?.DeliveryFallbackAddressId ?? string.Empty,
            DeliveryFallbackAddressType = extras?.DeliveryFallbackAddressType ?? string.Empty,
            NyxLarkUnionId = extras?.NyxLarkUnionId ?? string.Empty,
            NyxLarkChatId = extras?.NyxLarkChatId ?? string.Empty,
            OriginalSenderAuthorization = sender is null || string.IsNullOrWhiteSpace(sender.OwnerScopeId) ? null :
                new AgentToolChannelSenderAuthorization
                {
                    BindingId = sender.BindingId,
                    OwnerScopeId = sender.OwnerScopeId,
                    Platform = sender.Subject.Platform,
                    Tenant = sender.Subject.Tenant,
                    ExternalUserId = sender.Subject.ExternalUserId,
                },
        };
    }

    private async Task<ConversationTurnResult> SendExternalCallbackReplyAsync(
        MessageContent content, string sentActivitySeed, ConversationReference? conversation,
        InboundMessage inbound, ChannelBotRegistrationEntry? registration,
        OutboundDeliveryContext delivery, CancellationToken ct)
    {
        if (registration is not { Tombstoned: false } || !IsRegistrationInboundMatch(inbound, registration))
            return ConversationTurnResult.PermanentFailure("callback_registration_mismatch", "The original Channel registration cannot deliver this reply.");

        var resolution = await ResolveExternalCallbackDeliveryKeyAsync(registration, ct);
        if (resolution.Key is null)
        {
            return resolution.Permanent
                ? ConversationTurnResult.PermanentFailure(resolution.ErrorCode, resolution.ErrorSummary)
                : ConversationTurnResult.TransientFailure(resolution.ErrorCode, resolution.ErrorSummary);
        }

        var sent = await _relayOutboundPort.SendWithAgentKeyAsync(ResolveRelayPlatform(inbound, conversation),
            conversation?.Clone() ?? new ConversationReference(), content, delivery, resolution.Key, ct);
        return sent.Success
            ? BuildRelaySentResult(sent.SentActivityId, sentActivitySeed, content, delivery)
            : ToRelayFailure(sent);
    }

    private static bool IsCallbackOriginRegistrationMatch(
        ChannelCallbackOrigin origin,
        ChannelBotRegistrationEntry registration,
        string? expectedConversationActorId)
    {
        if (origin is null || origin.OriginalActivity is null ||
            !string.Equals(origin.ChannelRegistrationId, registration.Id, StringComparison.Ordinal) ||
            !string.Equals(origin.ConversationActorId, expectedConversationActorId, StringComparison.Ordinal))
            return false;

        return IsRegistrationActivityMatch(origin.OriginalActivity, registration);
    }

    private static bool IsRegistrationActivityMatch(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration)
    {
        if (!IsCanonicalRegistrationPlatform(activity, registration))
            return false;

        if (!IsRegistrationBotMatch(activity, registration))
            return false;

        return IsRegistrationTransportMatch(activity.TransportExtras, registration);
    }

    private static bool IsCallbackReplyRegistrationAvailable(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration) =>
        IsCanonicalRegistrationPlatform(activity, registration) &&
        IsRegistrationBotMatch(activity, registration);

    private static bool IsRegistrationBotMatch(ChatActivity activity, ChannelBotRegistrationEntry registration)
    {
        if (activity.Bot is not { Value.Length: > 0 } bot ||
            string.Equals(bot.Value, registration.Id, StringComparison.Ordinal))
            return true;

        // Direct adapters use registration IDs; NyxID Relay uses the Agent API Key ID.
        // Accept the Relay identity only when its typed transport key corroborates it.
        return !string.IsNullOrWhiteSpace(registration.NyxAgentApiKeyId) &&
               string.Equals(bot.Value, registration.NyxAgentApiKeyId, StringComparison.Ordinal) &&
               string.Equals(activity.TransportExtras?.NyxAgentApiKeyId, registration.NyxAgentApiKeyId, StringComparison.Ordinal);
    }

    private static bool IsRegistrationInboundMatch(
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration)
    {
        try
        {
            if (!string.Equals(
                    Aevatar.Foundation.Abstractions.ChannelPlatformId.FromCanonical(inbound.Platform).Value,
                    Aevatar.Foundation.Abstractions.ChannelPlatformId.FromCanonical(registration.Platform).Value,
                    StringComparison.Ordinal))
                return false;
        }
        catch (ArgumentException)
        {
            return false;
        }

        return IsRegistrationTransportMatch(inbound.TransportExtras, registration);
    }

    private static bool IsRegistrationTransportMatch(
        TransportExtras? extras,
        ChannelBotRegistrationEntry registration)
    {
        if (extras is null)
            return true;

        return (string.IsNullOrWhiteSpace(extras.NyxAgentApiKeyId) ||
                string.Equals(extras.NyxAgentApiKeyId, registration.NyxAgentApiKeyId, StringComparison.Ordinal)) &&
               (string.IsNullOrWhiteSpace(extras.NyxRegistrationScopeId) ||
                string.Equals(extras.NyxRegistrationScopeId, registration.ScopeId, StringComparison.Ordinal));
    }

    private async Task<ExternalCallbackDeliveryKeyResolution> ResolveExternalCallbackDeliveryKeyAsync(
        ChannelBotRegistrationEntry registration,
        CancellationToken ct)
    {
        var vault = _toolServiceProvider.GetService<ISecretVault>();
        if (vault is null || !ChannelWorkflowResultDeliveryCapability.TryGetDeliveryCredential(
                registration, out var subject, out var reference) || reference is null)
            return ExternalCallbackDeliveryKeyResolution.PermanentResult(
                "callback_delivery_credential_missing",
                "The original Channel registration has no usable delivery credential.");

        try
        {
            var resolved = await vault.ResolveAsync(new ResolveSecretRequest(reference.Ref, reference.Purpose,
                registration.ScopeId, subject, "channel-external-callback-reply"), ct);
            if (resolved.Resolved && reference.Equals(resolved.Reference) && !string.IsNullOrWhiteSpace(resolved.Secret))
                return ExternalCallbackDeliveryKeyResolution.Success(resolved.Secret);

            if (resolved.FailureReason == SecretResolutionFailureReason.NotFound)
                return ExternalCallbackDeliveryKeyResolution.Transient(
                    "callback_agent_key_unavailable",
                    "The original Channel registration credential is temporarily unavailable.");

            return ExternalCallbackDeliveryKeyResolution.PermanentResult(
                "callback_delivery_credential_missing",
                "The original Channel registration credential is missing or no longer valid.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Callback delivery credential resolution failed transiently: registration={RegistrationId}",
                registration.Id);
            return ExternalCallbackDeliveryKeyResolution.Transient(
                "callback_agent_key_unavailable",
                "The original Channel registration credential is temporarily unavailable.");
        }
    }

    private readonly record struct ExternalCallbackDeliveryKeyResolution(
        string? Key,
        bool Permanent,
        string ErrorCode,
        string ErrorSummary)
    {
        public static ExternalCallbackDeliveryKeyResolution Success(string key) => new(key, false, string.Empty, string.Empty);

        public static ExternalCallbackDeliveryKeyResolution PermanentResult(string errorCode, string errorSummary) =>
            new(null, true, errorCode, errorSummary);

        public static ExternalCallbackDeliveryKeyResolution Transient(string errorCode, string errorSummary) =>
            new(null, false, errorCode, errorSummary);
    }
}
