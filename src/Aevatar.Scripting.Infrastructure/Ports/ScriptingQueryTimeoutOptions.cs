namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptingQueryTimeoutOptions
{
    private static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(45);

    public TimeSpan DefinitionSnapshotQueryTimeout { get; init; } = DefaultQueryTimeout;

    public TimeSpan CatalogEntryQueryTimeout { get; init; } = DefaultQueryTimeout;

    public TimeSpan ResolveDefinitionSnapshotQueryTimeout() =>
        ScriptingTimeoutValueNormalizer.NormalizeOrDefault(
            DefinitionSnapshotQueryTimeout,
            DefaultQueryTimeout);

    public TimeSpan ResolveCatalogEntryQueryTimeout() =>
        ScriptingTimeoutValueNormalizer.NormalizeOrDefault(
            CatalogEntryQueryTimeout,
            DefaultQueryTimeout);
}
