namespace Aevatar.Foundation.Abstractions.ExternalLinks;

/// <summary>
/// Transport-level contract for a single external connection.
/// Each protocol (WebSocket, gRPC stream, MQTT, TCP) implements this.
/// </summary>
public interface IExternalLinkTransport : IAsyncDisposable
{
    string TransportType { get; }

    Task ConnectAsync(ExternalLinkDescriptor descriptor, CancellationToken ct);
    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);

    /// <summary>
    /// Set by the runtime. Called on I/O thread when data arrives.
    /// Must NOT directly modify actor state — only dispatch events.
    /// </summary>
    Func<ReadOnlyMemory<byte>, CancellationToken, Task>? OnMessageReceived { set; }

    /// <summary>
    /// Set by the runtime. Called on I/O thread when connection state changes.
    /// </summary>
    Func<ExternalLinkStateChange, string?, CancellationToken, Task>? OnStateChanged { set; }
}

public enum ExternalLinkStateChange
{
    Connected,
    Disconnected,
    Error,
    Closed
}
