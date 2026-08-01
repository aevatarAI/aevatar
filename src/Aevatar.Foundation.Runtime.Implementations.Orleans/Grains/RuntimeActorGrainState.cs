using Aevatar.Foundation.Abstractions.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

[GenerateSerializer]
public sealed class RuntimeActorGrainState
{
    [Id(0)]
    public string AgentId { get; set; } = string.Empty;

    [Obsolete("AgentTypeName is a reserved Orleans state slot. Runtime identity is RuntimeActorIdentity.Kind.")]
    [Id(1)]
    public string? AgentTypeName { get; set; }

    [Id(2)]
    public string? ParentId { get; set; }

    [Id(3)]
    public List<string> Children { get; set; } = [];

    [Id(4)]
    public string? AgentStateTypeName { get; set; }

    [Id(5)]
    public byte[]? AgentStateSnapshot { get; set; }

    [Id(6)]
    public long AgentStateSnapshotVersion { get; set; }

    /// <summary>
    /// Stable business identity (kind + schema version). Once an actor row
    /// exists, <see cref="RuntimeActorIdentity.Kind"/> is the only runtime
    /// identity input.
    /// </summary>
    [Id(7)]
    public RuntimeActorIdentity? Identity { get; set; }

    /// <summary>
    /// Protobuf-encoded runtime delivery checkpoint for committed-state observation.
    /// It is deliberately separate from the actor's business state snapshot.
    /// </summary>
    [Id(8)]
    public byte[]? CommittedStatePublicationState { get; set; }
}
