using Aevatar.Foundation.Abstractions.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

[GenerateSerializer]
public sealed class RuntimeActorGrainState
{
    [Id(0)]
    public string AgentId { get; set; } = string.Empty;

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
    /// Stable business identity (kind + schema version). Populated lazily
    /// during the Phase 1 transition: empty for legacy rows, written on
    /// first successful activation that resolves the persisted
    /// <see cref="AgentTypeName"/> to a registered kind. <see cref="AgentTypeName"/>
    /// stays alongside until Phase 3 hard-deprecation so mixed-version pods
    /// safely coexist during gradual rollouts.
    /// </summary>
    [Id(7)]
    public RuntimeActorIdentity? Identity { get; set; }
}
