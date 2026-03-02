namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class DefaultScriptingPortTimeouts : IScriptingPortTimeouts
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

    public TimeSpan DefinitionSnapshotQueryTimeout => DefaultTimeout;

    public TimeSpan CatalogEntryQueryTimeout => DefaultTimeout;

    public TimeSpan EvolutionDecisionTimeout => DefaultTimeout;
}
