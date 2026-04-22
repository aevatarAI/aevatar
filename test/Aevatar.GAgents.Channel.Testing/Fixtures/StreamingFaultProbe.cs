namespace Aevatar.GAgents.Channel.Testing;

/// <summary>
/// Adapter-supplied probe for streaming fault scenarios that are only observable through adapter-specific introspection.
/// </summary>
/// <remarks>
/// RFC §5.8 requires that streaming adapters (a) mark interrupted messages when <c>DisposeAsync</c> runs before
/// <c>CompleteAsync</c>, (b) reach a terminal state even when the intent degrades mid-stream, and (c) treat
/// <c>AppendAsync</c> as idempotent by <see cref="StreamChunk.SequenceNumber"/>. The adapter is the only component with
/// visibility into "was this message marked interrupted" or "did the stream reach a terminal state", so tests delegate
/// both the scenario drive and the observation to this probe.
/// </remarks>
public abstract class StreamingFaultProbe
{
    /// <summary>
    /// Begins a streaming reply, appends a chunk, disposes the handle without calling <c>CompleteAsync</c>, and returns
    /// whether the adapter recorded the message as interrupted.
    /// </summary>
    public abstract Task<bool> DisposeWithoutCompleteMarksInterruptedAsync(CancellationToken ct = default);

    /// <summary>
    /// Begins a streaming reply whose intent becomes degraded mid-stream and returns whether the handle reached a
    /// terminal state (completed or interrupted) rather than staying stuck.
    /// </summary>
    public abstract Task<bool> IntentDegradesMidwayReachesTerminalStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Replays a chunk with the same <see cref="StreamChunk.SequenceNumber"/> twice and returns whether the adapter
    /// treated the replay as idempotent (not a duplicate emission of the same content) without suppressing later
    /// chunks that happen to carry identical text but a distinct sequence number.
    /// </summary>
    public abstract Task<bool> AppendIdempotentBySequenceNumberAsync(CancellationToken ct = default);
}
