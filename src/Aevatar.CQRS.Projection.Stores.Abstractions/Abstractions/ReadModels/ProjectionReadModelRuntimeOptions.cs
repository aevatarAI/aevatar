namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class ProjectionReadModelRuntimeOptions : IProjectionStoreSelectionRuntimeOptions
{
    public ProjectionReadModelMode Mode { get; set; } = ProjectionReadModelMode.CustomReadModel;

    public string DocumentProvider { get; set; } = ProjectionReadModelProviderNames.InMemory;

    public string GraphProvider { get; set; } = ProjectionReadModelProviderNames.InMemory;

    public bool FailOnUnsupportedCapabilities { get; set; } = true;

    string IProjectionStoreSelectionRuntimeOptions.DocumentProvider => DocumentProvider;

    string IProjectionStoreSelectionRuntimeOptions.GraphProvider => GraphProvider;

    bool IProjectionStoreSelectionRuntimeOptions.FailOnUnsupportedCapabilities => FailOnUnsupportedCapabilities;

    ProjectionReadModelMode IProjectionStoreSelectionRuntimeOptions.ReadModelMode => Mode;
}
