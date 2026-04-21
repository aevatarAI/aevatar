using System.Threading.Channels;

namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Owns adapter lifecycle and exposes the normalized inbound stream consumed by the channel pipeline.
/// </summary>
public interface IChannelTransport
{
    /// <summary>
    /// Gets the channel this transport instance serves.
    /// </summary>
    ChannelId Channel { get; }

    /// <summary>
    /// Gets the transport mode used by the adapter.
    /// </summary>
    TransportMode TransportMode { get; }

    /// <summary>
    /// Gets the declared capability matrix for this transport instance.
    /// </summary>
    ChannelCapabilities Capabilities { get; }

    /// <summary>
    /// Initializes the transport with the stable binding data for one bot registration.
    /// </summary>
    Task InitializeAsync(ChannelTransportBinding binding, CancellationToken ct);

    /// <summary>
    /// Starts receiving inbound activities.
    /// </summary>
    Task StartReceivingAsync(CancellationToken ct);

    /// <summary>
    /// Stops receiving inbound activities and releases transport-owned resources.
    /// </summary>
    Task StopReceivingAsync(CancellationToken ct);

    /// <summary>
    /// Gets the single-consumer inbound stream of normalized activities emitted by this transport.
    /// </summary>
    ChannelReader<ChatActivity> InboundStream { get; }
}
