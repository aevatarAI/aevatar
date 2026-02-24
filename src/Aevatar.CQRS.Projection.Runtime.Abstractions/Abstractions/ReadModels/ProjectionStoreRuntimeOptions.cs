namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public sealed class ProjectionStoreRuntimeOptions : IProjectionStoreSelectionRuntimeOptions
{
    public ProjectionStoreMode Mode { get; set; } = ProjectionStoreMode.Custom;

    public string DocumentProvider { get; set; } = ProjectionProviderNames.InMemory;

    public string GraphProvider { get; set; } = ProjectionProviderNames.InMemory;

    public bool FailOnUnsupportedCapabilities { get; set; } = true;

    string IProjectionStoreSelectionRuntimeOptions.DocumentProvider => DocumentProvider;

    string IProjectionStoreSelectionRuntimeOptions.GraphProvider => GraphProvider;

    bool IProjectionStoreSelectionRuntimeOptions.FailOnUnsupportedCapabilities => FailOnUnsupportedCapabilities;

    ProjectionStoreMode IProjectionStoreSelectionRuntimeOptions.StoreMode => Mode;
}
