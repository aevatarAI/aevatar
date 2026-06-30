using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Platform.Lark.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Sends a single actor-approved SkillRunner output snapshot to Lark, using POST for the first
/// snapshot and PUT edits for later snapshots against the captured message id.
/// </summary>
/// <remarks>
/// Refactor (iter15/cluster-027-streaming-reply-timer-business-dispatch):
///   Old pattern: timer callback directly inspected/mutated pending output and performed Lark POST/PUT from callback timing
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

    /// <summary>
    /// Lark error code for "the message has reached the number of times it can be edited".
    /// This is terminal for the message — it never recovers on retry — so the sink seals the
    /// message (stops editing) and delivers the complete text as a fresh POST at finalize,
    /// instead of PUT-storming and throwing (which re-fired the run and spammed the chat).
    /// </summary>
    private const int LarkEditCapReachedCode = 230072;

    private readonly ILarkOutboundDispatcher _outboundDispatcher;
    private readonly LarkSendNewMessageRequest _initialMessageTemplate;
    private readonly NyxIdApiClient _editClient;
    private readonly Func<int?, string, string> _rejectionMessageBuilder;
    private readonly ILogger? _logger;
    private string? _platformMessageId;
    private bool _editCapReached;
    private bool _disposed;

    public SkillRunnerStreamingReplySink(
        ILarkOutboundDispatcher outboundDispatcher,
        LarkSendNewMessageRequest initialMessageTemplate,
        Func<int?, string, string> rejectionMessageBuilder,
        ILogger? logger,
        NyxIdApiClient editClient)
    {
        ArgumentNullException.ThrowIfNull(outboundDispatcher);
        ArgumentNullException.ThrowIfNull(initialMessageTemplate);
        ArgumentNullException.ThrowIfNull(rejectionMessageBuilder);
        ArgumentNullException.ThrowIfNull(editClient);

        _outboundDispatcher = outboundDispatcher;
        _initialMessageTemplate = initialMessageTemplate;
        _editClient = editClient;
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

        // Once Lark's per-message edit cap (230072) is hit, the message can never be edited
        // again. Suppress further mid-stream snapshots; the complete text is delivered as a
        // fresh POST at finalize (the second branch condition below) so the run completes
        // instead of PUT-storming + throwing.
        if (_editCapReached && !isFinal)
            return;

        if (_platformMessageId is null || (_editCapReached && isFinal))
        {
            LarkSendNewMessageResult result;
            try
            {
                result = await SendInitialAsync(capped, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!isFinal && IsTransientFailure(ex, ct))
            {
                _logger?.LogWarning(
                    ex,
                    "SkillRunner streaming sink: initial Lark POST threw mid-stream; will retry on next actor-approved snapshot. slug={Slug}",
                    _initialMessageTemplate.NyxProviderSlug);
                return;
            }

            if (result.MessageId is not null)
            {
                // After an edit-cap seal we POST the complete text as a NEW message at
                // finalize; do not re-capture the id (there is nothing left to edit).
                if (!_editCapReached)
                    _platformMessageId = result.MessageId;
                ChunksEmitted++;
                return;
            }

            if (isFinal)
                throw new InvalidOperationException(_rejectionMessageBuilder(result.LarkCode, result.Detail));

            _logger?.LogWarning(
                "SkillRunner streaming sink: initial Lark POST rejected mid-stream (lark_code={LarkCode}, detail={Detail}); next actor-approved snapshot will retry. slug={Slug}",
                result.LarkCode,
                result.Detail,
                _initialMessageTemplate.NyxProviderSlug);
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
                _initialMessageTemplate.NyxProviderSlug);
            return;
        }

        if (edit.succeeded)
        {
            ChunksEmitted++;
            return;
        }

        // Lark's per-message edit-count cap is terminal for THIS message and never recovers
        // on retry. Seal it: stop editing, and deliver the complete text as a fresh message so
        // the run completes instead of throwing → retrying → re-firing → spamming the chat.
        if (edit.larkCode == LarkEditCapReachedCode)
        {
            _editCapReached = true;
            if (!isFinal)
            {
                _logger?.LogWarning(
                    "SkillRunner streaming sink: Lark edit cap reached (lark_code={LarkCode}); sealing message — the final snapshot will post a fresh message. slug={Slug}",
                    edit.larkCode,
                    _initialMessageTemplate.NyxProviderSlug);
                return;
            }

            var fresh = await SendInitialAsync(capped, ct).ConfigureAwait(false);
            if (fresh.MessageId is not null)
            {
                ChunksEmitted++;
                return;
            }

            throw new InvalidOperationException(_rejectionMessageBuilder(fresh.LarkCode, fresh.Detail));
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
            _initialMessageTemplate.NyxProviderSlug);
    }

    private async Task<LarkSendNewMessageResult> SendInitialAsync(string text, CancellationToken ct) =>
        await _outboundDispatcher.SendNewMessageAsync(
            _initialMessageTemplate with
            {
                ContentJson = JsonSerializer.Serialize(new { text }),
            },
            ct).ConfigureAwait(false);

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

        var response = await _editClient.ProxyRequestAsync(
            _initialMessageTemplate.NyxProxyBearerToken,
            _initialMessageTemplate.NyxProviderSlug,
            $"open-apis/im/v1/messages/{Uri.EscapeDataString(platformMessageId)}",
            "PUT",
            body,
            null,
            ct).ConfigureAwait(false);

        if (LarkProxyResponse.TryGetError(response, out var larkCode, out var detail))
            return (false, larkCode, detail);

        return (true, null, string.Empty);
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
