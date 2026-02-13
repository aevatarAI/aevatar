// ─────────────────────────────────────────────────────────────
// OrleansAgentState - Grain persistent state model.
// Stores metadata only; business state uses IStateStore<TState>.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Orleans.Grain;

/// <summary>
/// Grain-level persistent metadata. Business state is managed by
/// <see cref="Aevatar.Persistence.IStateStore{TState}"/> and event sourcing.
/// </summary>
[GenerateSerializer]
public sealed class OrleansAgentState
{
    /// <summary>Assembly-qualified agent type name for reactivation.</summary>
    [Id(0)] public string? AgentTypeName { get; set; }

    /// <summary>Logical agent identifier.</summary>
    [Id(1)] public string AgentId { get; set; } = "";

    /// <summary>Parent actor ID, or null when no parent exists.</summary>
    [Id(2)] public string? ParentId { get; set; }

    /// <summary>Child actor IDs.</summary>
    [Id(3)] public List<string> Children { get; set; } = [];
}
