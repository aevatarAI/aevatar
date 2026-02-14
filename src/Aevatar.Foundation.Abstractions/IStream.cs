// ─────────────────────────────────────────────────────────────
// IStream - stream contract.
// Event broadcast channel for publishing and subscribing to Protobuf messages.
// ─────────────────────────────────────────────────────────────

using Google.Protobuf;

namespace Aevatar.Foundation.Abstractions;

/// <summary>
/// Stream contract for producing and subscribing to Protobuf messages.
/// </summary>
public interface IStream
{
    /// <summary>Unique stream identifier.</summary>
    string StreamId { get; }

    /// <summary>Produces a message to the stream.</summary>
    /// <typeparam name="T">Message type, must implement Protobuf IMessage.</typeparam>
    Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage;

    /// <summary>Subscribes to messages of a specific type and returns a disposable subscription.</summary>
    /// <typeparam name="T">Message type, must implement Protobuf IMessage.</typeparam>
    Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler,
        CancellationToken ct = default) where T : IMessage, new();
}
