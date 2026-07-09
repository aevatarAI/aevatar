using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Platform.Lark;

public sealed class LarkChannelRelayTailTextSender : IChannelRelayTailTextSender
{
    private readonly ILarkOutboundDispatcher _larkOutboundDispatcher;
    private readonly ILogger<LarkChannelRelayTailTextSender> _logger;

    public LarkChannelRelayTailTextSender(
        ILarkOutboundDispatcher larkOutboundDispatcher,
        ILogger<LarkChannelRelayTailTextSender> logger)
    {
        _larkOutboundDispatcher = larkOutboundDispatcher ?? throw new ArgumentNullException(nameof(larkOutboundDispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ChannelRelayTailTextSendResult> SendTailSegmentsAsync(
        ChannelRelayTailTextSendRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsLarkPlatform(request.Platform))
        {
            return ChannelRelayTailTextSendResult.Failed(
                "relay_tail_segment_platform_unsupported",
                "Tail text segmentation is only supported for Lark relay deliveries.");
        }

        var providerSlug = Normalize(request.TransportExtras?.NyxProviderSlug);
        if (providerSlug is null)
        {
            return ChannelRelayTailTextSendResult.Failed(
                "lark_tail_segment_provider_missing",
                "Lark outbound provider slug is missing for tail text segment delivery.");
        }

        if (string.IsNullOrWhiteSpace(request.NyxProxyCredential))
        {
            return ChannelRelayTailTextSendResult.Failed(
                "lark_tail_segment_credential_missing",
                "NyxID user access token is missing for Lark tail text segment delivery.");
        }

        var target = LarkConversationTargets.BuildFromInboundWithFallback(
            request.ChatType,
            request.ConversationId,
            request.SenderId,
            request.TransportExtras?.NyxLarkUnionId,
            request.TransportExtras?.NyxLarkChatId);
        if (string.IsNullOrWhiteSpace(target.Primary.ReceiveId) ||
            string.IsNullOrWhiteSpace(target.Primary.ReceiveIdType))
        {
            return ChannelRelayTailTextSendResult.Failed(
                "lark_tail_segment_target_missing",
                "Lark receive target is missing for tail text segment delivery.");
        }

        foreach (var segment in request.TailSegments)
        {
            var contentJson = JsonSerializer.Serialize(new { text = segment });
            LarkSendNewMessageResult result;
            try
            {
                result = await _larkOutboundDispatcher.SendNewMessageAsync(
                        new LarkSendNewMessageRequest(
                            NyxApiKey: request.NyxProxyCredential,
                            providerSlug,
                            "text",
                            contentJson,
                            target.Primary,
                            target.Fallback),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Lark tail text segment send threw. correlation={CorrelationId}",
                    request.CorrelationId);
                return ChannelRelayTailTextSendResult.Failed(
                    "relay_tail_segment_send_failed",
                    ex.Message);
            }

            if (!result.Succeeded)
            {
                return ChannelRelayTailTextSendResult.Failed(
                    "relay_tail_segment_send_failed",
                    string.IsNullOrWhiteSpace(result.Detail) ? "Lark tail text segment send failed." : result.Detail,
                    FailureKind.PermanentAdapterError,
                    result.LarkCode ?? 0);
            }
        }

        return ChannelRelayTailTextSendResult.Success();
    }

    private static bool IsLarkPlatform(string? platform) =>
        string.Equals(platform, "lark", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(platform, "feishu", StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
