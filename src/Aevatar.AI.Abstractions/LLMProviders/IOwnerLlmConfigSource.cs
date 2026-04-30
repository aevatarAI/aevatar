namespace Aevatar.AI.Abstractions.LLMProviders;

/// <summary>
/// Narrow port for reading the bot owner's pre-configured LLM model + route + max-tool-rounds
/// before pinning them onto outbound LLM request metadata. Both scheduled agents
/// (SkillRunnerGAgent, WorkflowAgentGAgent) and channel-bot turn runners (NyxidChat) consume
/// this contract via <see cref="OwnerLlmConfigApplier"/>; the host (Mainnet, CLI, demos)
/// supplies the implementation by bridging whichever upstream config store it composes
/// (typically <c>IUserConfigQueryPort</c> from the Studio.Application package). Lives in the
/// AI abstractions layer so neither agent package needs to depend on Studio.Application or on
/// each other just to read three string fields.
/// </summary>
public interface IOwnerLlmConfigSource
{
    /// <summary>
    /// Resolves the owner's LLM config for the given scope id. Implementations must never throw
    /// for "not configured" — they return a record whose fields are empty / zero so the helper
    /// quietly falls through to provider defaults. Transient projection failures may throw; the
    /// helper catches and logs without bubbling to the agent's execution turn.
    /// </summary>
    Task<OwnerLlmConfig> GetForScopeAsync(string scopeId, CancellationToken ct = default);
}

/// <summary>
/// Plain-data view of the three LLM-config fields agents pin onto outbound LLM metadata: a
/// model id override (matches <c>LLMRequestMetadataKeys.ModelOverride</c>), a NyxID route
/// preference (matches <c>NyxIdRoutePreference</c>), and a tool-round cap (matches
/// <c>MaxToolRoundsOverride</c>). Adapters fill these from whatever upstream user-config record
/// the host has wired up.
/// </summary>
public sealed record OwnerLlmConfig(
    string? DefaultModel,
    string? PreferredLlmRoute,
    int MaxToolRounds)
{
    public static OwnerLlmConfig Empty { get; } = new(null, null, 0);
}
