using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Platform.Lark.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Lark;

/// <summary>
/// Edits existing Lark text messages through the NyxID proxy.
/// </summary>
public sealed class LarkTextMessageEditPort : ILarkTextMessageEditPort
{
    private readonly NyxIdApiClient _client;
    private readonly ILogger<LarkTextMessageEditPort> _logger;

    public LarkTextMessageEditPort(
        NyxIdApiClient client,
        ILogger<LarkTextMessageEditPort>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger<LarkTextMessageEditPort>.Instance;
    }

    public async Task<LarkTextMessageEditResult> EditAsync(
        LarkTextMessageEditRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var body = JsonSerializer.Serialize(new
        {
            msg_type = "text",
            content = JsonSerializer.Serialize(new { text = request.Text }),
        });

        var response = await _client.ProxyRequestAsync(
            request.NyxProxyBearerToken,
            request.NyxProviderSlug,
            $"open-apis/im/v1/messages/{Uri.EscapeDataString(request.MessageId)}",
            "PUT",
            body,
            extraHeaders: null,
            ct).ConfigureAwait(false);

        if (LarkProxyResponse.TryGetError(response, out var larkCode, out var detail))
        {
            _logger.LogWarning(
                "Lark text message edit rejected: messageId={MessageId} larkCode={LarkCode} detail={Detail}",
                request.MessageId,
                larkCode,
                detail);
            return LarkTextMessageEditResult.Failed(larkCode, detail);
        }

        return LarkTextMessageEditResult.Success();
    }

    private static void Validate(LarkTextMessageEditRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NyxProxyBearerToken))
            throw new ArgumentException("NyxID proxy bearer token is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.NyxProviderSlug))
            throw new ArgumentException("NyxID provider slug is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MessageId))
            throw new ArgumentException("Lark message id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Lark message text is required.", nameof(request));
    }
}
