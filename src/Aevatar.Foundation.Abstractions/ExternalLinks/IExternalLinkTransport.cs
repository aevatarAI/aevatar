namespace Aevatar.Foundation.Abstractions.ExternalLinks;

public interface IExternalLinkSignalSink
{
    Task PublishMessageReceivedAsync(ExternalLinkMessageReceivedSignal signal, CancellationToken ct);
    Task PublishStateChangedAsync(ExternalLinkTransportStateChangedSignal signal, CancellationToken ct);
}

/// <summary>
/// Transport-level contract for a single external connection.
/// Each protocol (WebSocket, gRPC stream, MQTT, TCP) implements this.
/// </summary>
// Refactor (iter56/cluster-912-external-link-signal-contract):
// old=transport direct callback, new=typed signal sink.
// Transport implementations publish protobuf internal signals only.
// Actor/module turns consume the signals and perform reconciliation.
public interface IExternalLinkTransport : IAsyncDisposable
{
    string TransportType { get; }

    Task ConnectAsync(ExternalLinkDescriptor descriptor, CancellationToken ct);
    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);

    IExternalLinkSignalSink? SignalSink { set; }
}

public enum ExternalLinkStateChange
{
    Connected,
    Disconnected,
    Error,
    Closed
}
