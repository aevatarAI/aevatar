using Google.Protobuf;

namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Encodes and decodes projection session events for stream transport.
/// </summary>
public interface IProjectionSessionEventCodec<TEvent>
{
    /// <summary>
    /// Stream channel namespace (prefix) used to isolate event families.
    /// </summary>
    string Channel { get; }

    string GetEventType(TEvent evt);

    ByteString Serialize(TEvent evt);

    TEvent? Deserialize(string eventType, ByteString payload);
}
