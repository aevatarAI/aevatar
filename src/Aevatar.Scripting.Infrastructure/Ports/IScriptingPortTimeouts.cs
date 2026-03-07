namespace Aevatar.Scripting.Infrastructure.Ports;

public interface IScriptingPortTimeouts
{
    TimeSpan DefinitionSnapshotQueryTimeout { get; }

    TimeSpan DefinitionMutationTimeout { get; }

    TimeSpan CatalogEntryQueryTimeout { get; }

    TimeSpan CatalogMutationTimeout { get; }

    TimeSpan EvolutionCommandAckTimeout { get; }

    TimeSpan EvolutionSnapshotQueryTimeout { get; }

    TimeSpan RuntimeSnapshotQueryTimeout { get; }
}
