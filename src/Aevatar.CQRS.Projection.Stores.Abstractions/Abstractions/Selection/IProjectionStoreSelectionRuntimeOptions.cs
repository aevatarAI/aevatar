namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionStoreSelectionRuntimeOptions
{
    string DocumentProvider { get; }

    string GraphProvider { get; }

    bool FailOnUnsupportedCapabilities { get; }

    ProjectionReadModelMode ReadModelMode { get; }
}
