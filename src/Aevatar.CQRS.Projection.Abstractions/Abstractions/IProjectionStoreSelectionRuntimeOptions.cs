namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionStoreSelectionRuntimeOptions
{
    string ReadModelProvider { get; }

    string RelationProvider { get; }

    bool FailOnUnsupportedCapabilities { get; }

    ProjectionReadModelMode ReadModelMode { get; }

    IReadOnlyDictionary<string, string> ReadModelBindings { get; }
}
