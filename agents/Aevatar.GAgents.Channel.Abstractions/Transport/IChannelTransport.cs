using System.Threading.Channels;

namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Owns the runtime-side adapter lifecycle and exposes the normalized inbound working buffer consumed by the channel pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The same concrete adapter instance is expected to implement both <see cref="IChannelTransport"/> and
/// <see cref="IChannelOutboundPort"/> so lifecycle, capability, and credential state remain shared.
/// </para>
/// <para>
/// <see cref="InitializeAsync"/> must succeed exactly once before <see cref="StartReceivingAsync"/>. The inbound stream is
/// a single-reader, in-process buffer and is not the durable ingress boundary.
/// </para>
/// </remarks>
public interface IChannelTransport
{
    /// <summary>
    /// Gets the stable channel identifier served by this adapter instance.
    /// </summary>
    ChannelId Channel { get; }

    /// <summary>
    /// Gets the transport mode used by the adapter to accept inbound traffic.
    /// </summary>
    TransportMode TransportMode { get; }

    /// <summary>
    /// Gets the declared capability matrix shared with the outbound contract implemented by the same adapter instance.
    /// </summary>
    ChannelCapabilities Capabilities { get; }

    /// <summary>
    /// Initializes the adapter with the stable binding data for one bot registration.
    /// </summary>
    /// <param name="binding">The bot descriptor, credential reference, and verification material that bootstrap the adapter.</param>
    /// <param name="ct">A token that cancels initialization.</param>
    /// <returns>A task that completes when initialization finishes.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when initialization is attempted more than once, or after the adapter has already started receiving.
    /// </exception>
    /// <remarks>
    /// This method binds the capability, credential, and lifecycle state later consumed by
    /// <see cref="IChannelOutboundPort"/>. Implementations must treat it as an exactly-once transition.
    /// </remarks>
    Task InitializeAsync(ChannelTransportBinding binding, CancellationToken ct);

    /// <summary>
    /// Starts receiving inbound activities after initialization has completed.
    /// </summary>
    /// <param name="ct">A token that cancels startup.</param>
    /// <returns>A task that completes when the adapter is ready to publish inbound activities.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the adapter has not been initialized, or when receive startup is attempted more than once.
    /// </exception>
    /// <remarks>
    /// Host startup is expected to call this method exactly once. After this transition succeeds, outbound methods on the
    /// same adapter instance may begin sending traffic.
    /// </remarks>
    Task StartReceivingAsync(CancellationToken ct);

    /// <summary>
    /// Stops receiving inbound activities and releases transport-owned resources.
    /// </summary>
    /// <param name="ct">A token that cancels shutdown.</param>
    /// <returns>A task that completes when the adapter has stopped accepting new inbound work.</returns>
    /// <remarks>
    /// Implementations must stop receiving exactly once, wait for in-flight outbound work they own to settle, and release
    /// transport resources. They are not required to drain <see cref="InboundStream"/> to empty before returning.
    /// </remarks>
    Task StopReceivingAsync(CancellationToken ct);

    /// <summary>
    /// Gets the single-consumer inbound stream of normalized activities emitted by this transport.
    /// </summary>
    /// <remarks>
    /// The adapter must not fan out this stream. The pipeline is the only supported reader, and ordering must remain stable
    /// for activities as the adapter normalizes them into <see cref="ChatActivity"/> values.
    /// </remarks>
    ChannelReader<ChatActivity> InboundStream { get; }
}
