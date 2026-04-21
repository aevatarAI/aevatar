namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Represents one adapter-owned streaming reply session.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AppendAsync"/> is idempotent by <see cref="StreamChunk.SequenceNumber"/>. Replays of the same sequence number
/// must be ignored, while equal text with different sequence numbers remains valid output.
/// </para>
/// <para>
/// <see cref="CompleteAsync"/> must be called at most once. <see cref="DisposeAsync"/> is the idempotent safety net for
/// interrupted streams and may run before or after completion.
/// </para>
/// </remarks>
public abstract class StreamingHandle : IAsyncDisposable
{
    /// <summary>
    /// Appends one sequenced chunk to the in-flight reply.
    /// </summary>
    /// <param name="chunk">The monotonic chunk emitted by the caller.</param>
    /// <returns>A task that completes when the adapter has accepted the chunk into its streaming state machine.</returns>
    public abstract Task AppendAsync(StreamChunk chunk);

    /// <summary>
    /// Finalizes the streaming reply with the completed message content.
    /// </summary>
    /// <param name="final">The final message content to persist as the completed reply.</param>
    /// <returns>A task that completes when the adapter has finalized the reply.</returns>
    public abstract Task CompleteAsync(MessageContent final);

    /// <summary>
    /// Disposes the handle and releases adapter-owned streaming resources.
    /// </summary>
    /// <returns>A task that completes when adapter-owned streaming resources have been released.</returns>
    public abstract ValueTask DisposeAsync();
}
