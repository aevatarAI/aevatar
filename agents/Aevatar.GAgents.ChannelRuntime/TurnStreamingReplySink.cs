using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Drives progressive (edit-in-place) rendering of an LLM reply for a single turn by dispatching
/// <see cref="LlmReplyStreamChunkEvent"/>s to the conversation actor that owns the relay reply
/// token. State is per-invocation (instance fields on the sink, never a service-level map) so
/// different turns run on different sink instances by construction.
/// </summary>
/// <remarks>
/// <para>
/// The sink is responsible only for accumulating and throttling deltas; placeholder send, edit
/// dispatch, and streaming disable/fallback decisions are all owned by the conversation actor.
/// </para>
/// <para>
/// Throttling rules:
/// <list type="bullet">
/// <item>The first delta and any delta that arrives after the throttle window has fully elapsed
/// dispatches immediately, so the user sees movement as soon as the LLM produces text.</item>
/// <item>Deltas inside an active throttle window do not get silently dropped — the latest
/// accumulated text is stashed in <c>_pendingText</c> and a deferred flush timer fires at window
/// close to publish it. Multiple deltas in the same window collapse onto the latest text.</item>
/// <item>While a dispatch is in flight, additional deltas (caller-driven or timer-driven) are
/// likewise stashed; the dispatch loop reflushes the most recent <c>_pendingText</c> after the
/// in-flight chunk completes (reflush-on-conflict).</item>
/// <item><see cref="FinalizeAsync"/> bypasses the throttle so the actor sees the complete text
/// once the stream ends; if a dispatch is in flight, the final text reflushes after it and
/// <see cref="FinalizeAsync"/> awaits the dispatch loop's drain signal before returning so the
/// caller (the inbox runtime) does not race the ready event past the final chunk.</item>
/// </list>
/// </para>
/// <para>
/// Concurrency: caller code (<c>NyxIdConversationReplyGenerator</c>) awaits each
/// <see cref="OnDeltaAsync"/> call serially, but the throttle timer fires on the
/// <see cref="TimeProvider"/>'s scheduling thread and may race with caller-driven flushes. All
/// mutable state is guarded by <c>_lock</c>; chunk dispatches are serialized through
/// <c>_dispatchInProgress</c> so the conversation actor observes a strict ordering of edits.
/// </para>
/// </remarks>
internal sealed class TurnStreamingReplySink : IStreamingReplySink, IDisposable
{
    private readonly IActor _targetActor;
    private readonly string _correlationId;
    private readonly string _registrationId;
    private readonly ChatActivity _activityTemplate;
    private readonly TimeSpan _throttle;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    private readonly object _lock = new();
    private string _lastEmittedText = string.Empty;
    private DateTimeOffset _lastEmitAt = DateTimeOffset.MinValue;
    private int _chunksEmitted;
    private string _pendingText = string.Empty;
    private bool _hasPending;
    private ITimer? _flushTimer;
    private bool _dispatchInProgress;
    private bool _disposed;
    // Signaled by the dispatch loop when it fully drains. FinalizeAsync awaits this when a
    // dispatch is already in flight so the caller does not race the inbox runtime's
    // LlmReplyReadyEvent past the final chunk dispatch (the ConversationGAgent
    // processed-command guard would otherwise drop the late chunk).
    private TaskCompletionSource<bool>? _drainTcs;

    public TurnStreamingReplySink(
        IActor targetActor,
        string correlationId,
        string registrationId,
        ChatActivity activityTemplate,
        TimeSpan throttle,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        _targetActor = targetActor ?? throw new ArgumentNullException(nameof(targetActor));
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Correlation id is required.", nameof(correlationId));
        _correlationId = correlationId.Trim();
        _registrationId = registrationId ?? string.Empty;
        _activityTemplate = activityTemplate ?? throw new ArgumentNullException(nameof(activityTemplate));
        _throttle = throttle < TimeSpan.Zero ? TimeSpan.Zero : throttle;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger;
    }

    public int ChunksEmitted
    {
        get
        {
            lock (_lock) return _chunksEmitted;
        }
    }

    public Task OnDeltaAsync(string accumulatedText, CancellationToken ct) =>
        FlushAsync(accumulatedText, isFinal: false, ct);

    /// <summary>
    /// Applies the final accumulated text, bypassing the throttle so the actor can drive the final
    /// edit once the stream ends. If a dispatch is already in flight, the final text is stashed and
    /// this call awaits the dispatch loop's drain signal so the final chunk is on the wire before
    /// the caller proceeds (the inbox runtime sends LlmReplyReadyEvent immediately after).
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
        // Unblock any FinalizeAsync caller still awaiting the drain signal. Disposing while a
        // finalize is in flight is treated as "drained" — no further dispatches will run, so
        // continuing to await would hang.
        signal?.TrySetResult(false);
    }

    private async Task FlushAsync(string text, bool isFinal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        string? toDispatch = null;
        Task? drainTask = null;

        lock (_lock)
        {
            if (_disposed)
                return;

            if (string.Equals(text, _lastEmittedText, StringComparison.Ordinal))
            {
                // Already on the wire; clear any deferred copy so the timer doesn't republish
                // identical content. Even for isFinal we can return here: the final text is
                // already the most recent dispatched chunk, so the actor will see it before
                // any subsequent ready event.
                _pendingText = string.Empty;
                _hasPending = false;
                return;
            }

            if (_dispatchInProgress)
            {
                // A dispatch is in flight. Stash the latest text; the dispatch loop's reflush
                // step picks up _pendingText after the in-flight chunk completes. No timer is
                // needed because the loop polls _hasPending after every dispatch.
                _pendingText = text;
                _hasPending = true;
                if (isFinal)
                {
                    // Block FinalizeAsync until the dispatch loop drains the stashed final text.
                    // Without this wait, ChannelLlmReplyInboxRuntime sends LlmReplyReadyEvent
                    // first and ConversationGAgent's processed-command guard drops the late
                    // final chunk.
                    _drainTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    drainTask = _drainTcs.Task;
                }
            }
            else
            {
                var elapsed = _timeProvider.GetUtcNow() - _lastEmitAt;
                if (isFinal || elapsed >= _throttle)
                {
                    CancelTimerLocked();
                    _pendingText = string.Empty;
                    _hasPending = false;
                    _dispatchInProgress = true;
                    toDispatch = text;
                }
                else
                {
                    // Inside the throttle window: stash the latest text and arm the deferred
                    // flush timer if it isn't already armed. Subsequent deltas in this same
                    // window will simply overwrite _pendingText (collapse on the latest
                    // accumulated text).
                    _pendingText = text;
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
            await DispatchLoopAsync(toDispatch, ct).ConfigureAwait(false);

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

            // A caller-driven dispatch is already in flight. Its reflush loop will pick up
            // _pendingText, so don't start a second concurrent dispatch.
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
            // Fire-and-forget: errors are caught inside DispatchLoopAsync so the timer never
            // surfaces an unobserved exception. CancellationToken.None because the per-turn
            // CancellationToken belongs to the caller's await chain, not the timer.
            _ = DispatchLoopAsync(toDispatch, CancellationToken.None);
        }
    }

    private async Task DispatchLoopAsync(string firstText, CancellationToken ct)
    {
        var current = firstText;
        TaskCompletionSource<bool>? drainSignal = null;
        try
        {
            while (true)
            {
                await DispatchOneAsync(current, ct).ConfigureAwait(false);

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

    private async Task DispatchOneAsync(string text, CancellationToken ct)
    {
        var chunk = new LlmReplyStreamChunkEvent
        {
            CorrelationId = _correlationId,
            RegistrationId = _registrationId,
            Activity = _activityTemplate.Clone(),
            AccumulatedText = text,
            ChunkAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(chunk),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = _targetActor.Id },
            },
        };

        try
        {
            await _targetActor.HandleEventAsync(envelope, ct).ConfigureAwait(false);
            lock (_lock)
            {
                _lastEmittedText = text;
                _lastEmitAt = _timeProvider.GetUtcNow();
                _chunksEmitted++;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Failed to dispatch LLM reply stream chunk to conversation actor; dropping. correlationId={CorrelationId}",
                _correlationId);
        }
    }

    private void CancelTimerLocked()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
    }
}
