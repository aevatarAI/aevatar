using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

internal sealed class FeishuCardOutboundMessageSender
{
    private readonly IUserAgentDeliveryTargetReader _deliveryTargetReader;
    private readonly NyxIdApiClient _nyxIdApiClient;
    private readonly ILarkOutboundDispatcher? _larkOutboundDispatcher;
    private readonly ILogger _logger;

    public FeishuCardOutboundMessageSender(
        IUserAgentDeliveryTargetReader deliveryTargetReader,
        NyxIdApiClient nyxIdApiClient,
        ILogger logger,
        ILarkOutboundDispatcher? larkOutboundDispatcher = null)
    {
        _deliveryTargetReader = deliveryTargetReader ?? throw new ArgumentNullException(nameof(deliveryTargetReader));
        _nyxIdApiClient = nyxIdApiClient ?? throw new ArgumentNullException(nameof(nyxIdApiClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _larkOutboundDispatcher = larkOutboundDispatcher;
    }

    public async Task<UserAgentDeliveryTarget> ResolveTargetAsync(
        string deliveryTargetId,
        string platformSubject,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Resolving Lark delivery target: deliveryTargetId={DeliveryTargetId}, platformSubject={PlatformSubject}",
            deliveryTargetId,
            platformSubject);

        var target = await _deliveryTargetReader.GetAsync(deliveryTargetId, cancellationToken);
        if (target == null)
        {
            _logger.LogWarning(
                "Lark delivery target resolution failed: deliveryTargetId={DeliveryTargetId}, platformSubject={PlatformSubject}",
                deliveryTargetId,
                platformSubject);
            throw new InvalidOperationException($"Agent delivery target not found: {deliveryTargetId}");
        }

        if (!string.Equals(target.Platform, "lark", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unsupported {platformSubject} platform: {target.Platform}");

        _logger.LogInformation(
            "Resolved Lark delivery target: deliveryTargetId={DeliveryTargetId}, platform={Platform}, conversationId={ConversationId}, nyxProviderSlug={NyxProviderSlug}, hasNyxApiKey={HasNyxApiKey}",
            deliveryTargetId,
            target.Platform,
            target.ConversationId,
            target.NyxProviderSlug,
            !string.IsNullOrWhiteSpace(target.NyxApiKey));

        return target;
    }

    public async Task SendTextMessageAsync(
        UserAgentDeliveryTarget target,
        string text,
        string emptyResponseMessage,
        string failurePrefix,
        CancellationToken cancellationToken) =>
        await SendMessageAsync(
            target,
            "text",
            JsonSerializer.Serialize(new { text }),
            emptyResponseMessage,
            failurePrefix,
            cancellationToken);

    public async Task SendInteractiveCardMessageAsync(
        UserAgentDeliveryTarget target,
        string cardJson,
        string emptyResponseMessage,
        string failurePrefix,
        CancellationToken cancellationToken)
        => await SendMessageAsync(
            target,
            "interactive",
            cardJson,
            emptyResponseMessage,
            failurePrefix,
            cancellationToken);

    private async Task SendMessageAsync(
        UserAgentDeliveryTarget target,
        string messageType,
        string contentJson,
        string emptyResponseMessage,
        string failurePrefix,
        CancellationToken cancellationToken)
    {
        var deliveryTarget = LarkConversationTargets.Resolve(
            target.LarkReceiveId,
            target.LarkReceiveIdType,
            target.ConversationId);
        if (deliveryTarget.FellBackToPrefixInference)
        {
            _logger.LogDebug(
                "Feishu card outbound sender resolved Lark receive target by prefix inference (legacy entry): agent={AgentId}, conversationId={ConversationId}, receiveIdType={ReceiveIdType}",
                target.AgentId,
                target.ConversationId,
                deliveryTarget.ReceiveIdType);
        }

        _logger.LogInformation(
            "Sending Lark outbound message: agent={AgentId}, messageType={MessageType}, receiveId={ReceiveId}, receiveIdType={ReceiveIdType}, nyxProviderSlug={NyxProviderSlug}",
            target.AgentId,
            messageType,
            deliveryTarget.ReceiveId,
            deliveryTarget.ReceiveIdType,
            target.NyxProviderSlug);

        var outcome = await TrySendWithFallbackAsync(
            target,
            messageType,
            contentJson,
            deliveryTarget,
            emptyResponseMessage,
            cancellationToken);

        if (!outcome.Succeeded)
        {
            _logger.LogWarning(
                "Lark outbound message rejected: agent={AgentId}, messageType={MessageType}, receiveId={ReceiveId}, receiveIdType={ReceiveIdType}, usedFallback={UsedFallback}, larkCode={LarkCode}, detail={Detail}",
                target.AgentId,
                messageType,
                outcome.AttemptedTarget.ReceiveId,
                outcome.AttemptedTarget.ReceiveIdType,
                outcome.UsedFallback,
                outcome.LarkCode,
                outcome.Detail);
            throw new InvalidOperationException(BuildLarkRejectionMessage(failurePrefix, outcome.LarkCode, outcome.Detail));
        }

        _logger.LogInformation(
            "Sent Lark outbound message: agent={AgentId}, messageType={MessageType}, messageId={MessageId}, receiveId={ReceiveId}, receiveIdType={ReceiveIdType}, usedFallback={UsedFallback}",
            target.AgentId,
            messageType,
            outcome.MessageId,
            outcome.AttemptedTarget.ReceiveId,
            outcome.AttemptedTarget.ReceiveIdType,
            outcome.UsedFallback);
    }

    private async Task<LarkSendNewMessageResult> TrySendWithFallbackAsync(
        UserAgentDeliveryTarget target,
        string messageType,
        string contentJson,
        LarkReceiveTarget primary,
        string emptyResponseMessage,
        CancellationToken cancellationToken)
    {
        var result = await ResolveLarkOutboundDispatcher().SendNewMessageAsync(
            new LarkSendNewMessageRequest(
                target.NyxApiKey,
                target.NyxProviderSlug,
                messageType,
                contentJson,
                primary,
                ResolveFallbackTarget(target)),
            cancellationToken);

        if (!result.Succeeded && string.IsNullOrWhiteSpace(result.Detail))
            throw new InvalidOperationException(emptyResponseMessage);

        return result;
    }

    private static LarkReceiveTarget? ResolveFallbackTarget(UserAgentDeliveryTarget target)
    {
        var fallbackId = target.LarkReceiveIdFallback?.Trim();
        var fallbackType = target.LarkReceiveIdTypeFallback?.Trim();
        return string.IsNullOrEmpty(fallbackId) || string.IsNullOrEmpty(fallbackType)
            ? null
            : new LarkReceiveTarget(fallbackId, fallbackType, FellBackToPrefixInference: false);
    }

    private ILarkOutboundDispatcher ResolveLarkOutboundDispatcher() =>
        _larkOutboundDispatcher ?? new LarkOutboundDispatcher(_nyxIdApiClient, _logger);

    private static string BuildLarkRejectionMessage(string failurePrefix, int? larkCode, string detail)
    {
        if (larkCode == LarkBotErrorCodes.OpenIdCrossApp)
        {
            return
                $"{failurePrefix} (code={larkCode}): {detail}. " +
                "This agent was created before cross-app union_id ingress existed; " +
                "delete it (`/agents` → Delete) and recreate it to pick up the cross-app safe target.";
        }

        if (larkCode == LarkBotErrorCodes.UserIdCrossTenant)
        {
            return
                $"{failurePrefix} (code={larkCode}): {detail}. " +
                "The outbound Lark app is in a different tenant than the inbound app, so " +
                "user-id translation is impossible. Delete the agent (`/agents` → Delete) and recreate " +
                "it so the new chat_id-preferred outbound path takes effect, or align the NyxID " +
                "`s/api-lark-bot` proxy with the channel-bot that received the inbound event.";
        }

        return larkCode is { } code
            ? $"{failurePrefix} (code={code}): {detail}"
            : $"{failurePrefix}: {detail}";
    }
}
