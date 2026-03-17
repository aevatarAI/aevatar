namespace Aevatar.Foundation.Core.Configurations;

/// <summary>
/// Versioned class-level defaults for one agent type.
/// </summary>
public sealed record AgentClassDefaultsSnapshot<TConfig>(
    TConfig Defaults,
    long Version)
    where TConfig : class, new();

/// <summary>
/// Provides class-level default configuration for one config type.
/// </summary>
public interface IAgentClassDefaultsProvider<TConfig>
    where TConfig : class, new()
{
    /// <summary>
    /// Returns latest class defaults snapshot for the specified agent class.
    /// </summary>
    ValueTask<AgentClassDefaultsSnapshot<TConfig>> GetSnapshotAsync(
        Type agentType,
        CancellationToken ct = default);
}

/// <summary>
/// Fallback provider used when host/infrastructure does not register class defaults.
/// </summary>
public sealed class NullAgentClassDefaultsProvider<TConfig> : IAgentClassDefaultsProvider<TConfig>
    where TConfig : class, new()
{
    /// <summary>Singleton instance.</summary>
    public static NullAgentClassDefaultsProvider<TConfig> Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<AgentClassDefaultsSnapshot<TConfig>> GetSnapshotAsync(
        Type agentType,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentType);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentClassDefaultsSnapshot<TConfig>(new TConfig(), 0));
    }
}
