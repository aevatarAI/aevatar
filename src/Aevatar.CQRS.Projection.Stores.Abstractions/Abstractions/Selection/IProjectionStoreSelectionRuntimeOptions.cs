namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionStoreSelectionRuntimeOptions
{
    string ReadModelProvider { get; }

    string RelationProvider { get; }

    bool FailOnUnsupportedCapabilities { get; }

    ProjectionReadModelMode ReadModelMode { get; }

    IReadOnlyDictionary<string, string> ReadModelBindings { get; }
}
