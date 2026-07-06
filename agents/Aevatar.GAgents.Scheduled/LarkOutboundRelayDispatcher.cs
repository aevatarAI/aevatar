using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Platform.Lark;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Channel-neutral facade over the Lark outbound dispatcher for relay packages that must not
/// depend on platform assemblies directly.
/// </summary>
public interface ILarkOutboundRelayDispatcher
{
    Task<LarkOutboundRelayResult> SendNewMessageAsync(
        LarkOutboundRelayRequest request,
        CancellationToken cancellationToken);
}

public sealed record LarkOutboundRelayRequest(
    string NyxApiKey,
    string NyxProviderSlug,
    string MessageType,
    string ContentJson,
    string PrimaryReceiveId,
    string PrimaryReceiveIdType,
    string? FallbackReceiveId,
    string? FallbackReceiveIdType);

public sealed record LarkOutboundRelayResult(
    bool Succeeded,
    int? LarkCode,
    string Detail)
{
    public static LarkOutboundRelayResult Sent() => new(true, null, string.Empty);

    public static LarkOutboundRelayResult Failed(int? larkCode, string detail) =>
        new(false, larkCode, detail);
}

public sealed class LarkOutboundRelayDispatcher : ILarkOutboundRelayDispatcher
{
    private readonly ILarkOutboundDispatcher _dispatcher;

    public LarkOutboundRelayDispatcher(
        NyxIdApiClient client,
        ILogger<LarkOutboundDispatcher> logger)
        : this(new LarkOutboundDispatcher(client, logger))
    {
    }

    public LarkOutboundRelayDispatcher(ILarkOutboundDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async Task<LarkOutboundRelayResult> SendNewMessageAsync(
        LarkOutboundRelayRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _dispatcher.SendNewMessageAsync(
            new LarkSendNewMessageRequest(
                request.NyxApiKey,
                request.NyxProviderSlug,
                request.MessageType,
                request.ContentJson,
                new LarkReceiveTarget(
                    request.PrimaryReceiveId,
                    request.PrimaryReceiveIdType,
                    FellBackToPrefixInference: false),
                BuildFallbackTarget(request)),
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded
            ? LarkOutboundRelayResult.Sent()
            : LarkOutboundRelayResult.Failed(result.LarkCode, result.Detail);
    }

    private static LarkReceiveTarget? BuildFallbackTarget(LarkOutboundRelayRequest request) =>
        string.IsNullOrWhiteSpace(request.FallbackReceiveId) ||
        string.IsNullOrWhiteSpace(request.FallbackReceiveIdType)
            ? null
            : new LarkReceiveTarget(
                request.FallbackReceiveId.Trim(),
                request.FallbackReceiveIdType.Trim(),
                FellBackToPrefixInference: false);
}
