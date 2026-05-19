using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Platform.Lark;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Sends a single actor-approved SkillRunner output snapshot to Lark, using POST for the first
/// snapshot and PUT edits for later snapshots against the captured message id.
/// </summary>
/// <remarks>
/// Refactor (iter15/cluster-027-streaming-reply-timer-business-dispatch):
///   Old pattern: timer callback directly inspects/mutates pending business output and dispatches actor command from callback thread
///   New principle: SkillRunnerGAgent owns pending output/throttle/finalization and calls this sink only for a decided send.
/// </remarks>
internal sealed class SkillRunnerStreamingReplySink : IDisposable
{
    /// <summary>
    /// Lark text body cap. The platform documents ~150K-char message bodies, but the JSON wrapper
    /// (<c>content = JsonSerialize(new { text = ... })</c>) plus padding for receive_id metadata
    /// pushes effective room well below that. Cap inputs at 30K — comfortably under the platform
    /// limit even for multi-byte UTF-8 — and append a short truncation marker so a runaway LLM
    /// run does not silently lose its tail or get rejected at edit-time after the first chunks
    /// already landed.
    /// </summary>
    public const int MaxLarkTextLength = 30_000;

    private const string TruncationMarker = "\n\n…[truncated]";

    private readonly NyxIdApiClient _client;
    // Proxy-scoped agent API key. Treat as a secret: NEVER include it in log messages,
    // exception messages, or anything that flows to the user.
    private readonly string _nyxApiKey;
    private readonly string _nyxProviderSlug;
    private readonly LarkReceiveTarget _primaryTarget;
    private readonly LarkReceiveTarget? _fallbackTarget;
    private readonly Func<int?, string, string> _rejectionMessageBuilder;
    private readonly ILogger? _logger;
    private string? _platformMessageId;
    private bool _disposed;

    public SkillRunnerStreamingReplySink(
        NyxIdApiClient client,
        string nyxApiKey,
        string nyxProviderSlug,
        LarkReceiveTarget primaryTarget,
        LarkReceiveTarget? fallbackTarget,
        Func<int?, string, string> rejectionMessageBuilder,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(rejectionMessageBuilder);
        if (string.IsNullOrWhiteSpace(nyxApiKey))
            throw new ArgumentException("NyxID API key is required.", nameof(nyxApiKey));
        if (string.IsNullOrWhiteSpace(nyxProviderSlug))
            throw new ArgumentException("NyxID provider slug is required.", nameof(nyxProviderSlug));

        _client = client;
        _nyxApiKey = nyxApiKey;
        _nyxProviderSlug = nyxProviderSlug;
        _primaryTarget = primaryTarget;
        _fallbackTarget = fallbackTarget;
        _rejectionMessageBuilder = rejectionMessageBuilder;
        _logger = logger;
    }

    public int ChunksEmitted { get; private set; }

    public string? PlatformMessageId => _platformMessageId;

    public void Dispose() => _disposed = true;

    public Task OnDeltaAsync(string accumulatedText, CancellationToken ct) =>
        DispatchAsync(accumulatedText, isFinal: false, ct);

    public Task FinalizeAsync(string finalText, CancellationToken ct) =>
        DispatchAsync(finalText, isFinal: true, ct);

    public async Task DispatchAsync(string text, bool isFinal, CancellationToken ct)
    {
        if (_disposed)
            return;

        var capped = TruncateForLark(text);
        if (string.IsNullOrWhiteSpace(capped))
            return;

        if (_platformMessageId is null)
        {
            (string? messageId, int? larkCode, string detail) result;
            try
            {
                result = await SendInitialAsync(capped, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!isFinal && IsTransientFailure(ex, ct))
            {
                _logger?.LogWarning(
                    ex,
                    "SkillRunner streaming sink: initial Lark POST threw mid-stream; will retry on next actor-approved snapshot. slug={Slug}",
                    _nyxProviderSlug);
                return;
            }

            if (result.messageId is not null)
            {
                _platformMessageId = result.messageId;
                ChunksEmitted++;
                return;
            }

            if (isFinal)
                throw new InvalidOperationException(_rejectionMessageBuilder(result.larkCode, result.detail));

            _logger?.LogWarning(
                "SkillRunner streaming sink: initial Lark POST rejected mid-stream (lark_code={LarkCode}, detail={Detail}); next actor-approved snapshot will retry. slug={Slug}",
                result.larkCode,
                result.detail,
                _nyxProviderSlug);
            return;
        }

        (bool succeeded, int? larkCode, string detail) edit;
        try
        {
            edit = await EditAsync(_platformMessageId, capped, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!isFinal && IsTransientFailure(ex, ct))
        {
            _logger?.LogWarning(
                ex,
                "SkillRunner streaming sink: Lark edit threw mid-stream; will retry on next actor-approved snapshot. slug={Slug}",
                _nyxProviderSlug);
            return;
        }

        if (edit.succeeded)
        {
            ChunksEmitted++;
            return;
        }

        if (isFinal)
        {
            // Final edit must succeed — the user's report is gated on this. Throw so
            // HandleTriggerAsync persists Failed and surfaces the recovery hint.
            throw new InvalidOperationException(_rejectionMessageBuilder(edit.larkCode, edit.detail));
        }

        _logger?.LogWarning(
            "SkillRunner streaming sink: Lark edit rejected mid-stream (lark_code={LarkCode}, detail={Detail}); next actor-approved snapshot will retry. slug={Slug}",
            edit.larkCode,
            edit.detail,
            _nyxProviderSlug);
    }

    /// <summary>
    /// First-attempt POST to <c>open-apis/im/v1/messages</c>. On a Lark <c>230002 bot not in
    /// chat</c> rejection retries once with the captured fallback target — same recovery
    /// behavior as <c>SkillRunnerGAgent.TrySendWithFallbackAsync</c>, kept in this sink so a
    /// streaming-edit run never regresses cross-app same-tenant deployments.
    /// </summary>
    private async Task<(string? MessageId, int? LarkCode, string Detail)> SendInitialAsync(string text, CancellationToken ct)
    {
        var primaryResponse = await SendPostAsync(_primaryTarget, text, ct).ConfigureAwait(false);
        var primaryParsed = TryParseSendResponse(primaryResponse);
        if (primaryParsed.MessageId is not null)
            return primaryParsed;

        if (primaryParsed.LarkCode != LarkBotErrorCodes.BotNotInChat || _fallbackTarget is null)
            return primaryParsed;

        _logger?.LogInformation(
            "SkillRunner streaming sink: primary Lark POST rejected as `bot not in chat` (230002); retrying once with fallback receive_id_type={FallbackType}",
            _fallbackTarget.Value.ReceiveIdType);

        var fallbackResponse = await SendPostAsync(_fallbackTarget.Value, text, ct).ConfigureAwait(false);
        return TryParseSendResponse(fallbackResponse);
    }

    private async Task<string> SendPostAsync(LarkReceiveTarget target, string text, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            receive_id = target.ReceiveId,
            msg_type = "text",
            content = JsonSerializer.Serialize(new { text }),
        });

        return await _client.ProxyRequestAsync(
            _nyxApiKey,
            _nyxProviderSlug,
            $"open-apis/im/v1/messages?receive_id_type={target.ReceiveIdType}",
            "POST",
            body,
            null,
            ct).ConfigureAwait(false);
    }

    private async Task<(bool Succeeded, int? LarkCode, string Detail)> EditAsync(string platformMessageId, string text, CancellationToken ct)
    {
        // Lark splits the edit-message verbs by msg_type: PUT /open-apis/im/v1/messages/{id}
        // edits text / post (rich text) and requires `msg_type` + `content` in the body; PATCH
        // on the same path is reserved for editing interactive cards.
        var body = JsonSerializer.Serialize(new
        {
            msg_type = "text",
            content = JsonSerializer.Serialize(new { text }),
        });

        var response = await _client.ProxyRequestAsync(
            _nyxApiKey,
            _nyxProviderSlug,
            $"open-apis/im/v1/messages/{Uri.EscapeDataString(platformMessageId)}",
            "PUT",
            body,
            null,
            ct).ConfigureAwait(false);

        if (LarkProxyResponse.TryGetError(response, out var larkCode, out var detail))
            return (false, larkCode, detail);

        return (true, null, string.Empty);
    }

    private static (string? MessageId, int? LarkCode, string Detail) TryParseSendResponse(string response)
    {
        if (LarkProxyResponse.TryGetError(response, out var larkCode, out var detail))
            return (null, larkCode, detail);

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return (null, null, "missing_data");
            if (!data.TryGetProperty("message_id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
                return (null, null, "missing_message_id");
            var id = idProp.GetString();
            if (string.IsNullOrWhiteSpace(id))
                return (null, null, "empty_message_id");
            return (id, null, string.Empty);
        }
        catch (JsonException)
        {
            return (null, null, "invalid_send_response_json");
        }
    }

    /// <summary>
    /// True when the exception should be swallowed mid-stream and retried on the next actor-owned
    /// dispatch decision.
    /// </summary>
    private static bool IsTransientFailure(Exception ex, CancellationToken callerCt) =>
        !(ex is OperationCanceledException && callerCt.IsCancellationRequested);

    internal static string TruncateForLark(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        if (text.Length <= MaxLarkTextLength)
            return text;
        var head = MaxLarkTextLength - TruncationMarker.Length;
        if (head < 0)
            head = 0;
        return text[..head] + TruncationMarker;
    }
}
