// ─────────────────────────────────────────────────────────────
// IGAgentGrain - Orleans Grain interface for agent lifecycle.
// [AlwaysInterleave] on read-only and hierarchy methods allows
// them to execute concurrently with HandleEventAsync.
// ─────────────────────────────────────────────────────────────

using Orleans.Concurrency;

namespace Aevatar.Orleans.Grain;

/// <summary>Orleans Grain interface wrapping an agent instance.</summary>
public interface IGAgentGrain : IGrainWithStringKey
{
    // ── Initialization ──

    /// <summary>Initializes the Grain with the specified agent type.</summary>
    /// <param name="agentTypeName">Assembly-qualified agent type name.</param>
    /// <returns>True if initialization succeeded.</returns>
    Task<bool> InitializeAgentAsync(string agentTypeName);

    /// <summary>Checks whether this Grain has been initialized.</summary>
    [AlwaysInterleave]
    Task<bool> IsInitializedAsync();

    // ── Event handling (non-interleaved: serial execution) ──

    /// <summary>Handles an event from MassTransit (sole entry point).</summary>
    /// <param name="envelopeBytes">Protobuf-serialized EventEnvelope.</param>
    Task HandleEventAsync(byte[] envelopeBytes);

    // ── Hierarchy management ([AlwaysInterleave]: metadata only) ──

    /// <summary>Adds a child actor ID.</summary>
    [AlwaysInterleave]
    Task AddChildAsync(string childId);

    /// <summary>Removes a child actor ID.</summary>
    [AlwaysInterleave]
    Task RemoveChildAsync(string childId);

    /// <summary>Sets the parent actor ID.</summary>
    [AlwaysInterleave]
    Task SetParentAsync(string parentId);

    /// <summary>Clears the parent relationship.</summary>
    [AlwaysInterleave]
    Task ClearParentAsync();

    /// <summary>Gets all child actor IDs.</summary>
    [AlwaysInterleave]
    Task<IReadOnlyList<string>> GetChildrenAsync();

    /// <summary>Gets the parent actor ID.</summary>
    [AlwaysInterleave]
    Task<string?> GetParentAsync();

    // ── Description ──

    /// <summary>Gets a human-readable agent description.</summary>
    [AlwaysInterleave]
    Task<string> GetDescriptionAsync();

    // ── Lifecycle ──

    /// <summary>Requests Grain deactivation.</summary>
    Task DeactivateAsync();
}
