namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class DefaultScriptingPortTimeouts : IScriptingPortTimeouts
{
    private static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DefaultMutationTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DefaultEvolutionCommandAckTimeout = TimeSpan.FromMinutes(5);

    public TimeSpan DefinitionSnapshotQueryTimeout => DefaultQueryTimeout;

    public TimeSpan DefinitionMutationTimeout => DefaultMutationTimeout;

    public TimeSpan CatalogEntryQueryTimeout => DefaultQueryTimeout;

    public TimeSpan CatalogMutationTimeout => DefaultMutationTimeout;

    public TimeSpan EvolutionCommandAckTimeout => DefaultEvolutionCommandAckTimeout;

    public TimeSpan EvolutionSnapshotQueryTimeout => DefaultQueryTimeout;

    public TimeSpan RuntimeSnapshotQueryTimeout => DefaultQueryTimeout;
}
