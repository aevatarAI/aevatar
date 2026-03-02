namespace Aevatar.Scripting.Hosting.Ports;

public interface IScriptingPortTimeouts
{
    TimeSpan DefinitionSnapshotQueryTimeout { get; }

    TimeSpan CatalogEntryQueryTimeout { get; }

    TimeSpan EvolutionDecisionTimeout { get; }
}
