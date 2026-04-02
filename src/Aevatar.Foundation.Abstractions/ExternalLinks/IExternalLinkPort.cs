using Google.Protobuf;

namespace Aevatar.Foundation.Abstractions.ExternalLinks;

/// <summary>
/// Actor-side outbound port for sending messages through an external link.
/// </summary>
public interface IExternalLinkPort
{
    /// <summary>Sends a protobuf message to the external service via the specified link.</summary>
    Task SendAsync(string linkId, IMessage payload, CancellationToken ct = default);

    /// <summary>Sends raw bytes to the external service via the specified link.</summary>
    Task SendRawAsync(string linkId, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Explicitly disconnects a link (no reconnect).</summary>
    Task DisconnectAsync(string linkId, CancellationToken ct = default);
}
