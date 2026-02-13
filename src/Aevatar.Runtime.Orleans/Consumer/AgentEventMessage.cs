// ─────────────────────────────────────────────────────────────
// AgentEventMessage - MassTransit message contract.
// Wraps a serialized EventEnvelope with routing metadata.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Orleans.Consumer;

/// <summary>
/// MassTransit message envelope carrying a serialized EventEnvelope
/// and the target actor ID for routing.
/// </summary>
[GenerateSerializer]
public sealed class AgentEventMessage
{
    /// <summary>Target actor/agent ID for routing.</summary>
    [Id(0)] public string TargetActorId { get; set; } = "";

    /// <summary>Protobuf-serialized EventEnvelope bytes.</summary>
    [Id(1)] public byte[] EnvelopeBytes { get; set; } = [];
}
