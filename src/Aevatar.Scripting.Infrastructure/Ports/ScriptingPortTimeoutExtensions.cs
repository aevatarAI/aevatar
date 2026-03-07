namespace Aevatar.Scripting.Infrastructure.Ports;

internal static class ScriptingPortTimeoutExtensions
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

    public static TimeSpan GetDefinitionSnapshotQueryTimeout(this IScriptingPortTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(timeouts);
        return NormalizeOrDefault(timeouts.DefinitionSnapshotQueryTimeout);
    }

    public static TimeSpan GetDefinitionMutationTimeout(this IScriptingPortTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(timeouts);
        return NormalizeOrDefault(timeouts.DefinitionMutationTimeout);
    }

    public static TimeSpan GetCatalogEntryQueryTimeout(this IScriptingPortTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(timeouts);
        return NormalizeOrDefault(timeouts.CatalogEntryQueryTimeout);
    }

    public static TimeSpan GetCatalogMutationTimeout(this IScriptingPortTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(timeouts);
        return NormalizeOrDefault(timeouts.CatalogMutationTimeout);
    }

    public static TimeSpan GetEvolutionCommandAckTimeout(this IScriptingPortTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(timeouts);
        return NormalizeOrDefault(timeouts.EvolutionCommandAckTimeout);
    }

    public static TimeSpan GetEvolutionSnapshotQueryTimeout(this IScriptingPortTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(timeouts);
        return NormalizeOrDefault(timeouts.EvolutionSnapshotQueryTimeout);
    }

    public static TimeSpan GetRuntimeSnapshotQueryTimeout(this IScriptingPortTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(timeouts);
        return NormalizeOrDefault(timeouts.RuntimeSnapshotQueryTimeout);
    }

    private static TimeSpan NormalizeOrDefault(TimeSpan timeout) =>
        timeout > TimeSpan.Zero
            ? timeout
            : DefaultTimeout;
}
