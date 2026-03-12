namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class DefaultScriptingPortTimeouts : IScriptingPortTimeouts
{
    private static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DefaultEvolutionDecisionTimeout = TimeSpan.FromSeconds(90);

    public TimeSpan DefinitionSnapshotQueryTimeout => DefaultQueryTimeout;

    public TimeSpan CatalogEntryQueryTimeout => DefaultQueryTimeout;

    public TimeSpan EvolutionDecisionTimeout => DefaultEvolutionDecisionTimeout;
}
