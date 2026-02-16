using System.Threading.Channels;

namespace Aevatar.Foundation.Runtime.Streaming;

/// <summary>
/// Runtime options for in-memory stream buffering and subscriber error behavior.
/// </summary>
public sealed class InMemoryStreamOptions
{
    /// <summary>Per-stream queue capacity.</summary>
    public int Capacity { get; set; } = 4096;

    /// <summary>Behavior when queue is full.</summary>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;

    /// <summary>
    /// Whether to rethrow subscriber exceptions and stop stream processing.
    /// </summary>
    public bool ThrowOnSubscriberError { get; set; }
}
