namespace Aevatar.CQRS.Projection.Abstractions;

public sealed class ProjectionReadModelRuntimeOptions : IProjectionStoreSelectionRuntimeOptions
{
    public ProjectionReadModelRuntimeOptions()
    {
        Bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public ProjectionReadModelMode Mode { get; set; } = ProjectionReadModelMode.CustomReadModel;

    public string Provider { get; set; } = ProjectionReadModelProviderNames.InMemory;

    public string RelationProvider { get; set; } = "";

    public bool FailOnUnsupportedCapabilities { get; set; } = true;

    public Dictionary<string, string> Bindings { get; }

    string IProjectionStoreSelectionRuntimeOptions.ReadModelProvider => Provider;

    string IProjectionStoreSelectionRuntimeOptions.RelationProvider => RelationProvider;

    bool IProjectionStoreSelectionRuntimeOptions.FailOnUnsupportedCapabilities => FailOnUnsupportedCapabilities;

    ProjectionReadModelMode IProjectionStoreSelectionRuntimeOptions.ReadModelMode => Mode;

    IReadOnlyDictionary<string, string> IProjectionStoreSelectionRuntimeOptions.ReadModelBindings => Bindings;
}
