namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionStoreSelectionRuntimeOptions
{
    string DocumentProvider { get; }

    string GraphProvider { get; }

    bool FailOnUnsupportedCapabilities { get; }

    ProjectionStoreMode StoreMode { get; }
}
