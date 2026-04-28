using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Drives progressive (edit-in-place) rendering of a SkillRunner output by sending the first
/// non-empty delta as a fresh Lark <c>POST /open-apis/im/v1/messages</c> and then editing the
/// returned <c>message_id</c> via <c>PATCH /open-apis/im/v1/messages/{id}</c> for every later
/// delta. State is per-invocation (instance fields on the sink, never a service-level map) so
/// different runs render to different messages by construction.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="TurnStreamingReplySink"/>, this sink owns the outbound transport directly.
/// SkillRunner runs ambiently (cron, manual <c>/run-agent</c>, retries) so there is no inbound
/// reply-token to bind to <c>/channel-relay/reply/update</c>; the closest equivalent is Lark's
/// own edit-own-message endpoint, reached through the same <c>s/api-lark-bot</c> proxy slug
/// that <c>SkillRunnerGAgent.SendOutputAsync</c> already uses.
/// </para>
/// <para>
/// Throttling rules mirror <see cref="TurnStreamingReplySink"/>:
/// <list type="bullet">
/// <item>The first delta and any delta that arrives after the throttle window has fully elapsed
/// dispatch immediately, so the user sees the message land and start growing as soon as the LLM
/// produces text.</item>
/// <item>Deltas inside an active throttle window do not get silently dropped — the latest
/// accumulated text is stashed in <c>_pendingText</c> and a deferred flush timer fires at window
/// close to publish it. Multiple deltas in the same window collapse onto the latest text.</item>
/// <item>While a dispatch is in flight, additional deltas (caller-driven or timer-driven) are
/// likewise stashed; the dispatch loop reflushes the most recent <c>_pendingText</c> after the
/// in-flight chunk completes (reflush-on-conflict).</item>
/// <item><see cref="FinalizeAsync"/> bypasses the throttle so the actor sees the complete text
/// once the stream ends; if a dispatch is in flight, the final text reflushes after it and
/// <see cref="FinalizeAsync"/> awaits the dispatch loop's drain signal before returning.</item>
/// </list>
/// </para>
/// <para>
/// Failure semantics:
/// <list type="bullet">
/// <item>Initial POST rejection (Lark 4xx / Nyx envelope error) → throws via the same
/// <c>BuildLarkRejectionMessage</c> hint that the non-streaming path uses, so legacy callers see
/// the same actionable recovery guidance. <c>230002 bot not in chat</c> on the initial POST
/// triggers a one-shot retry against the captured fallback target before throwing.</item>
/// <item>Mid-stream PATCH rejection → logged and dropped. The next delta will retry the latest
/// accumulated text, and the LLM is still producing chunks; pulling the rug on the run because
/// of one rate-limit blip would force a full re-execute and lose the on-screen progress.</item>
/// <item>Final PATCH (or the standalone POST when no deltas ever streamed) → throws on
/// rejection. The caller persists <c>SkillRunnerExecutionFailedEvent</c> and surfaces the hint
/// in <c>/agent-status</c>.</item>
/// </list>
/// </para>
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
    private readonly string _nyxApiKey;
    private readonly string _nyxProviderSlug;
    private readonly LarkReceiveTarget _primaryTarget;
    private readonly LarkReceiveTarget? _fallbackTarget;
    private readonly Func<int?, string, string> _rejectionMessageBuilder;
    private readonly TimeSpan _throttle;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    private readonly object _lock = new();
    private string? _platformMessageId;
    private string _lastEmittedText = string.Empty;
    private DateTimeOffset _lastEmitAt = DateTimeOffset.MinValue;
    private int _chunksEmitted;
    private string _pendingText = string.Empty;
    private bool _hasPending;
    private ITimer? _flushTimer;
    private bool _dispatchInProgress;
    private bool _disposed;
    // Signaled by the dispatch loop when it fully drains. FinalizeAsync awaits this when a
    // dispatch is already in flight so the caller does not race past the final chunk.
    private TaskCompletionSource<bool>? _drainTcs;

    public SkillRunnerStreamingReplySink(
        NyxIdApiClient client,
        string nyxApiKey,
        string nyxProviderSlug,
        LarkReceiveTarget primaryTarget,
        LarkReceiveTarget? fallbackTarget,
        Func<int?, string, string> rejectionMessageBuilder,
        TimeSpan throttle,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(rejectionMessageBuilder);
        ArgumentNullException.ThrowIfNull(timeProvider);
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
        _throttle = throttle < TimeSpan.Zero ? TimeSpan.Zero : throttle;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public int ChunksEmitted
    {
        get
        {
            lock (_lock) return _chunksEmitted;
        }
    }

    public string? PlatformMessageId
    {
        get
        {
            lock (_lock) return _platformMessageId;
        }
    }

    public Task OnDeltaAsync(string accumulatedText, CancellationToken ct) =>
        FlushAsync(accumulatedText, isFinal: false, ct);

    /// <summary>
    /// Applies the final accumulated text, bypassing the throttle so the user sees the complete
    /// daily report once the LLM finishes. If a dispatch is already in flight, the final text is
    /// stashed and this call awaits the dispatch loop's drain signal so the final chunk is on
    /// the wire before the caller proceeds.
    /// </summary>
    public Task FinalizeAsync(string finalText, CancellationToken ct) =>
        FlushAsync(finalText, isFinal: true, ct);

    public void Dispose()
    {
        TaskCompletionSource<bool>? signal;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _flushTimer?.Dispose();
            _flushTimer = null;
            _hasPending = false;
            _pendingText = string.Empty;
            signal = _drainTcs;
            _drainTcs = null;
        }
        // Disposing while a finalize is in flight is treated as "drained" — no further dispatches
        // will run, so continuing to await would hang.
        signal?.TrySetResult(false);
    }

    private async Task FlushAsync(string text, bool isFinal, CancellationToken ct)
    {
        var capped = TruncateForLark(text);
        if (string.IsNullOrWhiteSpace(capped))
            return;

        string? toDispatch = null;
        Task? drainTask = null;

        lock (_lock)
        {
            if (_disposed)
                return;

            if (string.Equals(capped, _lastEmittedText, StringComparison.Ordinal))
            {
                // Already on the wire; clear any deferred copy. Even for isFinal we can return
                // here because the latest dispatched text is already the final text.
                _pendingText = string.Empty;
                _hasPending = false;
                return;
            }

            if (_dispatchInProgress)
            {
                _pendingText = capped;
                _hasPending = true;
                if (isFinal)
                {
                    _drainTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    drainTask = _drainTcs.Task;
                }
            }
            else
            {
                var elapsed = _timeProvider.GetUtcNow() - _lastEmitAt;
                if (isFinal || elapsed >= _throttle)
                {
                    // First-chunk dispatches immediately because _lastEmitAt starts at
                    // DateTimeOffset.MinValue, so `elapsed >= _throttle` is trivially true.
                    // Final-chunk also bypasses the throttle so the user sees the complete
                    // daily once the LLM finishes.
                    CancelTimerLocked();
                    _pendingText = string.Empty;
                    _hasPending = false;
                    _dispatchInProgress = true;
                    toDispatch = capped;
                }
                else
                {
                    _pendingText = capped;
                    _hasPending = true;
                    if (_flushTimer is null)
                    {
                        var delay = _throttle - elapsed;
                        if (delay < TimeSpan.Zero)
                            delay = TimeSpan.Zero;
                        _flushTimer = _timeProvider.CreateTimer(
                            OnFlushTimerFired,
                            state: null,
                            dueTime: delay,
                            period: Timeout.InfiniteTimeSpan);
                    }
                }
            }
        }

        if (toDispatch is not null)
            await DispatchLoopAsync(toDispatch, isFinal, ct).ConfigureAwait(false);

        if (drainTask is not null)
            await drainTask.ConfigureAwait(false);
    }

    private void OnFlushTimerFired(object? state)
    {
        string? toDispatch = null;

        lock (_lock)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;

            if (_disposed || !_hasPending)
                return;

            if (_dispatchInProgress)
                return;

            if (string.Equals(_pendingText, _lastEmittedText, StringComparison.Ordinal))
            {
                _pendingText = string.Empty;
                _hasPending = false;
                return;
            }

            _dispatchInProgress = true;
            toDispatch = _pendingText;
            _pendingText = string.Empty;
            _hasPending = false;
        }

        if (toDispatch is not null)
        {
            _ = DispatchLoopAsync(toDispatch, firstIsFinal: false, CancellationToken.None);
        }
    }

    private async Task DispatchLoopAsync(string firstText, bool firstIsFinal, CancellationToken ct)
    {
        var current = firstText;
        var currentIsFinal = firstIsFinal;
        TaskCompletionSource<bool>? drainSignal = null;
        try
        {
            while (true)
            {
                await DispatchOneAsync(current, currentIsFinal, ct).ConfigureAwait(false);

                string? next;
                lock (_lock)
                {
                    if (_disposed || !_hasPending)
                    {
                        _dispatchInProgress = false;
                        drainSignal = _drainTcs;
                        _drainTcs = null;
                        break;
                    }

                    if (string.Equals(_pendingText, _lastEmittedText, StringComparison.Ordinal))
                    {
                        _pendingText = string.Empty;
                        _hasPending = false;
                        _dispatchInProgress = false;
                        drainSignal = _drainTcs;
                        _drainTcs = null;
                        break;
                    }

                    next = _pendingText;
                    _pendingText = string.Empty;
                    _hasPending = false;
                    // A reflushed dispatch following a final-stash always carries the final text,
                    // so propagate the final-flag so the throw-on-failure semantics still fire.
                    currentIsFinal = _drainTcs is not null;
                }

                current = next!;
            }
        }
        catch (Exception ex)
        {
            TaskCompletionSource<bool>? errSignal;
            lock (_lock)
            {
                _dispatchInProgress = false;
                errSignal = _drainTcs;
                _drainTcs = null;
            }
            errSignal?.TrySetException(ex);
            throw;
        }

        drainSignal?.TrySetResult(true);
    }

    private async Task DispatchOneAsync(string text, bool isFinal, CancellationToken ct)
    {
        bool needsInitialSend;
        lock (_lock)
        {
            needsInitialSend = _platformMessageId is null;
        }

        if (needsInitialSend)
        {
            (string? messageId, int? larkCode, string detail) result;
            try
            {
                result = await SendInitialAsync(text, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!isFinal && ex is not OperationCanceledException)
            {
                // Mid-stream transport exception (timeout, network blip): log and let the next
                // delta retry. We only escalate at finalize because that is the moment the run's
                // success is gated.
                _logger?.LogWarning(
                    ex,
                    "SkillRunner streaming sink: initial Lark POST threw mid-stream; will retry on next delta. slug={Slug}",
                    _nyxProviderSlug);
                return;
            }

            if (result.messageId is not null)
            {
                lock (_lock)
                {
                    _platformMessageId = result.messageId;
                    _lastEmittedText = text;
                    _lastEmitAt = _timeProvider.GetUtcNow();
                    _chunksEmitted++;
                }
                return;
            }

            if (isFinal)
            {
                throw new InvalidOperationException(_rejectionMessageBuilder(result.larkCode, result.detail));
            }

            _logger?.LogWarning(
                "SkillRunner streaming sink: initial Lark POST rejected mid-stream (lark_code={LarkCode}, detail={Detail}); next delta will retry. slug={Slug}",
                result.larkCode,
                result.detail,
                _nyxProviderSlug);
            return;
        }

        (bool succeeded, int? larkCode, string detail) edit;
        try
        {
            edit = await EditAsync(_platformMessageId!, text, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!isFinal && ex is not OperationCanceledException)
        {
            _logger?.LogWarning(
                ex,
                "SkillRunner streaming sink: Lark edit threw mid-stream; will retry on next delta. slug={Slug}",
                _nyxProviderSlug);
            return;
        }

        if (edit.succeeded)
        {
            lock (_lock)
            {
                _lastEmittedText = text;
                _lastEmitAt = _timeProvider.GetUtcNow();
                _chunksEmitted++;
            }
            return;
        }

        if (isFinal)
        {
            // Final edit must succeed — the user's report is gated on this. Throw so
            // HandleTriggerAsync persists Failed and surfaces the recovery hint.
            throw new InvalidOperationException(_rejectionMessageBuilder(edit.larkCode, edit.detail));
        }

        _logger?.LogWarning(
            "SkillRunner streaming sink: Lark edit rejected mid-stream (lark_code={LarkCode}, detail={Detail}); next delta will retry. slug={Slug}",
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
        // Lark's edit-message endpoint takes only `content` — msg_type is fixed at creation time.
        var body = JsonSerializer.Serialize(new
        {
            content = JsonSerializer.Serialize(new { text }),
        });

        var response = await _client.ProxyRequestAsync(
            _nyxApiKey,
            _nyxProviderSlug,
            $"open-apis/im/v1/messages/{Uri.EscapeDataString(platformMessageId)}",
            "PATCH",
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

    private static string TruncateForLark(string text)
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

    private void CancelTimerLocked()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
    }
}
