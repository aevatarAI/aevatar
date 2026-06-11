using System.Threading.Channels;

namespace Aevatar.Foundation.Runtime.Streaming;

// DEV/TEST ONLY transport options - production must use a durable Orleans/Kafka stream provider.
// Refactor (iter109/cluster-109-inmemory-stream-inline-dispatch):
//   Old pattern: Local stream runtime keeps actor-id stream registries and can fan out subscribers via fire-and-forget background work.
//   New principle: InMemoryStream is dev/test-only transport (usage proves no production registration); keep stream/forwarding registry but remove concurrent subscriber fire-and-forget dispatch; no new admission abstraction.
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
