using Google.Protobuf;

namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Encodes and decodes projection session events for stream transport.
/// </summary>
// Refactor (iter34/cluster-003-projection-session-legacy-payload):
//   Old pattern: Projection session event transport carries both protobuf bytes and legacy string payload compatibility (legacy_payload string field in proto + legacy codec interface + legacy payload write path).
//   New principle: Projection session event transport carries protobuf payload only; obsolete legacy codec surface is deleted; tests/docs updated; protobuf legacy_payload field reserved per protobuf evolution rules; no concrete codec depended on the legacy interface.
public interface IProjectionSessionEventCodec<TEvent>
    where TEvent : class
{
    /// <summary>
    /// Stream channel namespace (prefix) used to isolate event families.
    /// </summary>
    string Channel { get; }

    string GetEventType(TEvent evt);

    ByteString Serialize(TEvent evt);

    TEvent? Deserialize(string eventType, ByteString payload);
}
