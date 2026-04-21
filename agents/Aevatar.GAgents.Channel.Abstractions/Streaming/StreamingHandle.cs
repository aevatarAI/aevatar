namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Represents one adapter-owned streaming reply session.
/// </summary>
public abstract class StreamingHandle : IAsyncDisposable
{
    /// <summary>
    /// Appends one sequenced chunk to the in-flight reply.
    /// </summary>
    public abstract Task AppendAsync(StreamChunk chunk);

    /// <summary>
    /// Finalizes the streaming reply with the completed message content.
    /// </summary>
    public abstract Task CompleteAsync(MessageContent final);

    /// <summary>
    /// Disposes the handle and releases adapter-owned streaming resources.
    /// </summary>
    public abstract ValueTask DisposeAsync();
}
