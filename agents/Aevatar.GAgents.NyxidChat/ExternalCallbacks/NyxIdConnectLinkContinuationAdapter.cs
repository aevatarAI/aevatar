using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.NyxidChat.ExternalCallbacks;

/// <summary>Channel-only create adapter. No route or owner fact is accepted from the model.</summary>
public sealed class NyxIdConnectLinkContinuationAdapter(
    IExternalCallbackCommandPort callbacks,
    TimeProvider timeProvider) : IChannelConnectLinkContinuationPort
{
    public async Task<ChannelConnectLinkCreateResult> CreateAsync(
        AgentToolExecutionContext context, ChannelConnectLinkCreateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var origin = context.Channel.Continuation;
        if (origin is null || string.IsNullOrWhiteSpace(origin.ConversationActorId) ||
            string.IsNullOrWhiteSpace(origin.ChannelRegistrationId) || string.IsNullOrWhiteSpace(origin.CanonicalConversationKey))
            return new(ErrorCode: "channel_continuation_context_missing");
        var authorization = Authorization(origin);
        if (!NyxIdConnectLinkVerifier.HasAuthorization(authorization))
            return new(ErrorCode: "original_sender_authorization_missing");
        if (!MatchesOriginalSender(context, origin, authorization!))
            return new(ErrorCode: "original_sender_authorization_mismatch");
        if (string.IsNullOrWhiteSpace(request.CatalogServiceSlug))
            return new(ErrorCode: "catalog_service_slug_required");

        // These limits are the existing NyxID Connect Link contract, not a new lifetime policy.
        var ttlSeconds = Math.Clamp(request.ExpiresInSeconds ?? 900, 60, 3600);
        var registration = await callbacks.AdmitAsync(new ExternalCallbackRegistration
        {
            CallbackId = Guid.NewGuid().ToString("N"), Kind = ExternalCallbackKind.ConnectLink,
            Origin = CreateOrigin(context, origin), Authorization = authorization,
            RequestedCatalogServiceSlug = request.CatalogServiceSlug.Trim(),
            ExpiresAtUnixMs = timeProvider.GetUtcNow().AddSeconds(ttlSeconds).ToUnixTimeMilliseconds(),
            ConnectLinkRequest = new ConnectLinkCreationRequest
            {
                Label = request.Label ?? string.Empty,
                RequestedBy = request.RequestedBy ?? string.Empty,
                ExpiresInSeconds = ttlSeconds,
            },
        }, ct).ConfigureAwait(false);
        // This tool executes inside an actor turn: acceptance cannot wait for another actor's
        // commit or HTTP create. The operation authority delivers the committed URL separately.
        return new(registration.CallbackId, Accepted: true);
    }

    private static CallbackAuthorizationReference? Authorization(AgentToolChannelContinuationContext origin)
    {
        var saved = origin.OriginalSenderAuthorization;
        return saved is null ? null : new CallbackAuthorizationReference
        {
            BindingId = saved.BindingId, OwnerScopeId = saved.OwnerScopeId,
            ExternalSubject = new ExternalSubjectRef
            { Platform = saved.Platform, Tenant = saved.Tenant, ExternalUserId = saved.ExternalUserId },
        };
    }

    private static bool MatchesOriginalSender(AgentToolExecutionContext context, AgentToolChannelContinuationContext origin,
        CallbackAuthorizationReference authorization) =>
        context.NyxIdAuthority.IsComplete &&
        string.Equals(authorization.ExternalSubject.Platform, context.NyxIdAuthority.Platform, StringComparison.Ordinal) &&
        string.Equals(authorization.ExternalSubject.Tenant, context.NyxIdAuthority.Tenant ?? string.Empty, StringComparison.Ordinal) &&
        string.Equals(authorization.ExternalSubject.ExternalUserId, context.NyxIdAuthority.ExternalUserId, StringComparison.Ordinal) &&
        string.Equals(authorization.BindingId, context.SenderBinding.BindingId, StringComparison.Ordinal) &&
        string.Equals(authorization.OwnerScopeId, context.Caller.OwnerScopeId, StringComparison.Ordinal) &&
        string.Equals(origin.ChannelRegistrationId, context.Channel.BotRegistrationId, StringComparison.Ordinal);

    private static ChannelCallbackOrigin CreateOrigin(AgentToolExecutionContext context, AgentToolChannelContinuationContext origin) => new()
    {
        ConversationActorId = origin.ConversationActorId,
        ChannelRegistrationId = origin.ChannelRegistrationId,
        ActionId = context.Request.CallId ?? context.Request.RequestId ?? origin.OriginalActivityId,
        OriginalActivity = new ChatActivity
        {
            Id = origin.OriginalActivityId, Type = ActivityType.Message,
            ChannelId = new ChannelId { Value = origin.ChannelId }, Bot = new BotInstanceId { Value = origin.BotId },
            Conversation = new ConversationReference
            {
                Channel = new ChannelId { Value = origin.ChannelId }, Bot = new BotInstanceId { Value = origin.BotId },
                CanonicalKey = origin.CanonicalConversationKey, Partition = origin.ConversationPartition,
                Scope = (ConversationScope)origin.ConversationScope,
            },
            From = new ParticipantRef { CanonicalId = origin.SenderId },
            Content = new MessageContent { Text = origin.OriginalUserText },
            OutboundDelivery = new OutboundDeliveryContext
            { ReplyMessageId = origin.ReplyMessageId, CorrelationId = origin.OutboundCorrelationId },
            TransportExtras = new TransportExtras
            {
                NyxAgentApiKeyId = origin.NyxAgentApiKeyId, NyxConversationId = origin.NyxConversationId,
                NyxPlatform = origin.Platform, NyxProviderSlug = origin.NyxProviderSlug,
                NyxRegistrationScopeId = context.Channel.RegistrationScopeId ?? string.Empty,
                NyxSenderUserId = context.SenderBinding.NyxUserId ?? string.Empty,
                DeliveryAddressId = origin.DeliveryAddressId, DeliveryAddressType = origin.DeliveryAddressType,
                DeliveryFallbackAddressId = origin.DeliveryFallbackAddressId,
                DeliveryFallbackAddressType = origin.DeliveryFallbackAddressType,
                NyxLarkUnionId = origin.NyxLarkUnionId, NyxLarkChatId = origin.NyxLarkChatId,
            },
        },
    };

}
