namespace Aevatar.Scripting.Infrastructure.Ports;

public interface IScriptingPortTimeouts
{
    TimeSpan DefinitionSnapshotQueryTimeout { get; }

    TimeSpan CatalogEntryQueryTimeout { get; }

    TimeSpan EvolutionDecisionTimeout { get; }
}
