namespace Aevatar.Scripting.Infrastructure.Ports;

internal static class ScriptingPortTimeoutExtensions
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

    public static TimeSpan GetDefinitionSnapshotQueryTimeout(this IScriptingPortTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(timeouts);
        return NormalizeOrDefault(timeouts.DefinitionSnapshotQueryTimeout);
    }

    public static TimeSpan GetCatalogEntryQueryTimeout(this IScriptingPortTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(timeouts);
        return NormalizeOrDefault(timeouts.CatalogEntryQueryTimeout);
    }

    public static TimeSpan GetEvolutionDecisionTimeout(this IScriptingPortTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(timeouts);
        return NormalizeOrDefault(timeouts.EvolutionDecisionTimeout);
    }

    private static TimeSpan NormalizeOrDefault(TimeSpan timeout) =>
        timeout > TimeSpan.Zero
            ? timeout
            : DefaultTimeout;
}
